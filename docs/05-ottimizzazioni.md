# 05 — Ottimizzazioni

## Principio

L'obiettivo primario non è il risparmio: è la **latenza** e l'**affidabilità**. Aggiungere una voce alla lista deve rispondere in decine di millisecondi e funzionare anche se Azure OpenAI ha un disservizio. Il risparmio economico è un effetto collaterale gradito.

Corollario: non ottimizzare le operazioni banali per il costo. Una chiamata a gpt-4o-mini con prompt corto costa centesimi ogni mille messaggi. Il peso vero sta nello **schema dei tool** ripetuto a ogni turno, e nel modello scelto.

## Router di intent

Architettura a tre livelli, in ordine di costo crescente.

```
messaggio in ingresso
   │
   ├─ L1  Comando esplicito (/lista, /spesa, callback_query)
   │      → handler diretto, 0 token, <10 ms
   │
   ├─ L2  Pattern deterministico con confidenza alta
   │      → handler diretto, 0 token, <50 ms
   │
   └─ L3  Fallback LLM con function calling
          → 3-5k token, 1-3 s
```

### L1 — comandi e callback

I comandi registrati via `setMyCommands` e i tap sulle inline keyboard sono già strutturati: non c'è nulla da interpretare. È il percorso di gran lunga più frequente per l'utente abituale, che impara le scorciatoie da solo.

Conseguenza progettuale: **investire nelle inline keyboard**. Dopo `/lista` il bot mostra le voci con un bottone per ciascuna; ogni spunta è un `callback_query` deterministico. Un utente che fa la spesa spunta 15 articoli senza generare una singola chiamata LLM.

### L2 — pattern matching

Copre le formulazioni frequenti.

```csharp
public interface IIntentMatcher
{
    string Intent { get; }
    string Culture { get; }                  // "it", "en" — le regex sono per lingua
    IntentMatch? TryMatch(string text);
}

public record IntentMatch(string Intent, double Confidence,
                          IReadOnlyDictionary<string, string> Slots);
```

**Il fast path esiste solo per italiano e inglese.** Le regex non sono traducibili e un matcher in una lingua che non si sa valutare è peggio di nessun matcher: produce falsi positivi silenziosi. Per le altre lingue si va sempre a L3, dove il modello è multilingua nativamente. Vedi [09-localizzazione.md](09-localizzazione.md).

Esempio per l'aggiunta alla lista:

```csharp
// aggiungi / metti / segna [X] [alla|in] lista
private static readonly Regex AddToList = new(
    @"^\s*(aggiungi|metti|segna|aggiungimi)\s+(?<item>.+?)(\s+(alla|in|nella)\s+lista.*)?$",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

**Copertura realistica: circa il 70% delle formulazioni.** Il restante 30% è quello che il pattern matching non prende, ed è esattamente quello che frustra l'utente:

- "ricordati che serve il pane"
- "finito il detersivo"
- "domani prendi anche le uova"
- "manca la carta igienica"
- "ah e il caffè"

Se il bot risponde "non ho capito" a una di queste, l'utente lo abbandona. Da qui la regola: **il fast path non deve mai essere l'unica strada.** Sotto una soglia di confidenza si passa a L3, sempre.

Soglia di partenza: 0.85. Sopra, si esegue direttamente; sotto, si passa all'LLM.

### Il caso in cui il fast path non conviene: le date

I promemoria richiedono di interpretare "giovedì", "fra due settimane", "il 15", "domani sera", "ogni lunedì". Le regex qui degradano male: ogni pattern aggiunto ne rompe un altro, e gli errori sono silenziosi — un promemoria fissato al giovedì sbagliato non dà errore, semplicemente arriva quando non serve.

Scelta: **il parsing delle date va sempre a L3**, tranne le forme banali (`/reminder 15/09 ...`). Il modello lo fa bene e il costo è irrilevante rispetto al danno di un promemoria alla data sbagliata.

Due accorgimenti che servono comunque:

- La data corrente e il `TimeZoneId` dell'utente vanno passati nel prompt (nella **coda variabile**, non nel system prompt, o si invalida la cache — vedi sotto)
- Il risultato va **riletto all'utente** nella conferma: "Ok, giovedì 6 agosto alle 9:00". È l'unico modo per intercettare l'interpretazione sbagliata prima che diventi un promemoria inutile

Vale anche il contrario: se una libreria di parsing di date in italiano copre bene i casi frequenti, usarla come L2 con confidenza alta e fallback a L3 è legittimo. Ma non vale scrivere quelle regex a mano.

### L3 — fallback LLM

Function calling su Azure OpenAI con gpt-4o-mini. Qui sta il costo vero, e va gestito con le tecniche sotto.

### Testare il router è essenziale

Il router è il componente con più probabilità di regredire silenziosamente: una regex modificata per coprire un caso rompe due altri. Servono test tabellari con un corpus crescente di frasi reali.

```csharp
[Theory]
[InlineData("aggiungi il latte", "shopping.add", "il latte")]
[InlineData("metti 2 litri di latte nella lista", "shopping.add", "2 litri di latte")]
[InlineData("segna pane", "shopping.add", "pane")]
[InlineData("ricordati che serve il pane", null, null)]      // → deve andare a L3
[InlineData("quanto ho speso a gennaio", "expenses.query", null)]
public void Router_classifica_correttamente(string input, string? intent, string? slot)
    => /* ... */;
```

Ogni frase che in produzione finisce a L3 quando poteva essere gestita a L2 va aggiunta al corpus. Il router migliora osservando il log, non ragionando a tavolino.

## Riduzione dei token nel percorso LLM

### Schema dei tool per contesto

Il peso maggiore del prompt è la definizione dei tool. Non serve mandarli tutti a ogni turno.

```csharp
// Male: 12 tool sempre presenti → ~4k token per turno
// Bene: tool filtrati per risorse accessibili nello spazio corrente
var tools = toolRegistry.ForSpace(space, membership.Permissions);
```

Se lo spazio corrente non ha il calendario collegato, gli 8 tool del calendario non entrano nel prompt. Su uno spazio di sola lista della spesa si scende da ~4k a ~800 token per turno: **fattore 5 sul costo del percorso L3.**

### Prompt caching

Il system prompt e lo schema dei tool sono identici a ogni turno per uno stesso spazio. Il prompt caching li fattura a tariffa ridotta dopo il primo turno.

Requisito: la parte cacheable deve stare **all'inizio** del prompt, invariata byte per byte. Ordine corretto:

```
[1] system prompt          ← statico, cacheable
[2] schema dei tool        ← statico per spazio, cacheable
[3] contesto dello spazio  ← semi-statico (nomi membri, categorie)
[4] storico conversazione  ← variabile
[5] messaggio corrente     ← variabile
```

Errore da evitare: inserire la data corrente o l'ora nel system prompt. Invalida la cache a ogni turno. Va passata come tool o nella coda variabile del prompt.

Stesso errore, meno ovvio: **tradurre il system prompt**. Un prompt per lingua significa una cache per lingua, e con pochi utenti per lingua la cache non si scalda mai. Il system prompt resta in inglese con l'istruzione a rispondere nella lingua dell'utente, e l'indicazione della lingua va nella coda variabile (blocco `[3]`). Vedi [09-localizzazione.md](09-localizzazione.md).

### Storico limitato

Non serve tutta la conversazione. Ultimi 6-8 turni, o ancora meglio uno stato conversazionale strutturato (`ConversationState`) invece del testo integrale. Un assistente per liste e calendario non ha bisogno di memoria conversazionale profonda: il contesto vero sta nel database.

TTL aggressivo sullo stato: 30 minuti. Dopo, si riparte pulito.

### Recupero dati via tool, non nel prompt

Tentazione: mettere la lista della spesa completa nel system prompt "così il modello la conosce". Sbagliato — cresce senza limite e invalida la cache a ogni modifica. La lista si legge con un tool quando serve.

## Latenza

| Intervento | Guadagno |
|---|---|
| Fast path L1/L2 | 2 s → 50 ms sulle operazioni frequenti |
| Streaming della risposta | Percepito, non reale — su Telegram si fa con `editMessageText` progressivo |
| `sendChatAction("typing")` immediato | Feedback in <100 ms, riduce l'ansia di attesa |
| Cache in memoria dei permessi | Evita 2-3 query per messaggio |
| Cache in memoria dei token OAuth | Evita una chiamata Key Vault per messaggio |
| Connection pooling EF | Attivo per default, ma verificare `MinPoolSize` con SQL serverless |

`sendChatAction` va inviato **prima** di iniziare l'elaborazione, non dopo: costa nulla e cambia la percezione.

## Cache: cosa e per quanto

| Dato | TTL | Note |
|---|---|---|
| `ChannelIdentity → User` | 15 min | Letto a ogni messaggio, cambia raramente |
| Permessi di membership | 5 min | Un cambio permessi che tarda 5 min è accettabile; invalidare esplicitamente dalla console |
| Token OAuth | fino a scadenza − 5 min | **In memoria, mai su disco.** Riduce le operazioni Key Vault |
| Categorie di spesa | 1 h | Quasi statiche |
| Schema dei tool per spazio | 1 h | Ricalcolo costoso, invarianti |

`IMemoryCache` è sufficiente con una singola istanza. Con più istanze serve valutare Redis — ma a quel punto serve anche Service Bus, ed è un altro momento del progetto.

## Costi SQL

- **Indici prima di tutto.** `(SpaceId, Date)` su `Expense` e `(ShoppingListId, IsChecked)` su `ShoppingItem` sono i due che contano; vedi [02-modello-dati.md](02-modello-dati.md).
- **Aggregazioni in SQL, non in memoria.** "Quanto ho speso a gennaio per categoria" è un `GROUP BY`, non un `ToList()` seguito da LINQ. Con poche centinaia di righe la differenza non si vede; con qualche anno di storico sì.
- **`AsNoTracking()`** su tutte le letture di sola visualizzazione.
- **Attenzione all'auto-pause.** Con SQL serverless, il primo accesso dopo la pausa costa secondi. Se il pattern d'uso è "sporadico ma sensibile alla latenza", il tier Basic a costo fisso è preferibile.

## Cosa non ottimizzare (ancora)

| Tentazione | Perché rimandarla |
|---|---|
| Modello locale (ONNX, Windows AI Foundry) per la classificazione | Interessante, ma su App Service non c'è GPU. Ha senso come sperimentazione a parte, non nell'MVP. |
| Embedding + ricerca semantica per gli intent | Aggiunge un servizio e latenza per un guadagno marginale rispetto a regex + fallback. |
| Normalizzazione dei nomi prodotto | Serve solo per lo storico prezzi (Fase 4). Prima serve sapere se qualcuno usa la lista. |
| Redis, Service Bus, più istanze | Nessuno dei tre serve sotto le poche decine di utenti. Introdurli presto significa manutenere infrastruttura senza beneficio. |
| Fine-tuning | Il function calling con un buon system prompt copre tutto il necessario. |

La regola per tutte: se non c'è una metrica in Application Insights che mostra il problema, l'ottimizzazione è prematura.

## Cosa misurare da subito

Application Insights, custom metrics, dal primo giorno:

- Distribuzione dei messaggi fra L1 / L2 / L3 — se L3 supera il 40%, il router va migliorato
- **Distribuzione L1/L2/L3 per lingua** — se una lingua sta quasi tutta su L3, o è priva di matcher (previsto) o i suoi matcher non funzionano
- Token consumati per turno, p50 e p95
- Latenza end-to-end per livello di router
- Intent riconosciuti vs "non ho capito" — il secondo numero è il più importante del prodotto
- **Lingue effettivamente in uso** — decide dove vale la pena investire in matcher L2
- Retention: utenti attivi al giorno 7 e al giorno 14

L'ultima riga è quella che decide se il progetto continua. Vedi [06-roadmap.md](06-roadmap.md).
