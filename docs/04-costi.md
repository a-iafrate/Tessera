# 04 — Costi

> **Tutti i numeri sono ordini di grandezza a memoria, riferiti al listino noto a maggio 2026, regione West Europe, IVA esclusa.** I prezzi Azure, Azure OpenAI e WhatsApp cambiano con regolarità. Prima di prendere decisioni, verificare su [Azure Pricing Calculator](https://azure.microsoft.com/pricing/calculator/) e sul listino Meta per il proprio paese.

## Riepilogo

| Scenario | Costo mensile |
|---|---|
| Fase 1 — MVP Telegram, ~10 utenti | **€25-40** |
| Fase 2 — con calendario, ~50 utenti | **€50-80** |
| Fase 3 — con WhatsApp, ~100 utenti | **€150-400** ⚠️ |

Il salto della Fase 3 non è infrastruttura: sono i template WhatsApp. È lì che il modello economico va deciso, non prima.

## Fase 1 — MVP

| Voce | SKU | €/mese |
|---|---|---|
| Azure Web App | App Service B1 (always-on) | 12-15 |
| Azure SQL | Serverless, 0.5-1 vCore, auto-pause | 5-15 |
| Key Vault | Standard, a operazione | ~1 |
| Application Insights | Primi 5 GB/mese gratuiti | 0 |
| Blob Storage | Scontrini, fase 4 | 0 |
| Azure OpenAI | gpt-4o-mini, vedi sotto | 3-10 |
| Dominio | ~15/anno | ~1 |
| **Totale** | | **~25-40** |

Note:

- **Always-on non è opzionale.** Senza, l'app va in idle e il primo webhook dopo l'inattività va in timeout. Il tier Free non lo supporta: B1 è il minimo.
- **Azure SQL serverless con auto-pause** è la scelta economica giusta, con l'avvertenza che il risveglio dalla pausa costa alcuni secondi di latenza. Se il bot "sembra lento al mattino", è quello. L'alternativa è il tier Basic a costo fisso (~€5) senza auto-pause, adeguato per l'MVP.
- **Key Vault** si paga a operazione: mettere in cache i token in memoria con TTL, non rileggerli a ogni messaggio.
- **Service Bus: non incluso.** Nell'MVP la coda è in memoria. Aggiungerlo costa ~€10/mese (Basic) quando servirà.

### Credito MVP

Il credito Azure incluso nel programma MVP (~$150/mese) copre interamente questa fase. La validazione dell'idea ha quindi costo marginale zero, il che rafforza la scelta di una Fase 1 lunga e di una Fase 3 rinviata.

## Costi LLM

È la voce che scala con l'uso, e l'unica dove la scelta del modello cambia l'ordine di grandezza.

### Costo per turno

Un turno con function calling porta nel prompt: system prompt + schema dei tool + storico conversazione + messaggio. Realisticamente **3-5k token di input**, poche centinaia in output.

Stima con **gpt-4o-mini** (~$0.15/1M input, ~$0.60/1M output):

| Utenti attivi | Msg/giorno/utente | Msg/mese | Costo LLM/mese |
|---|---|---|---|
| 10 | 20 | 6.000 | €3-5 |
| 50 | 20 | 30.000 | €15-25 |
| 100 | 20 | 60.000 | €30-40 |
| 500 | 20 | 300.000 | €150-200 |

Stessa colonna con **gpt-4o** pieno (~$2.50/1M input): moltiplicare per circa 16. A 100 utenti si superano i **€600/mese**. La differenza fra i due modelli è la differenza fra un progetto sostenibile e uno che non lo è.

### Cosa costa davvero

| Operazione | Token input | Costo (mini) |
|---|---|---|
| "aggiungi il latte" via fast path | 0 | **€0** |
| "aggiungi il latte" via LLM, prompt minimo | ~300 | ~€0,00005 |
| "che ho in lista?" via query SQL | 0 | **€0** |
| "sposta la riunione con Marco e avvisalo" | 4-6k | ~€0,001 |
| Scontrino via vision | 1-2k + immagine | ~€0,002-0,005 |

**Il fast path conta più per la latenza che per il costo.** Una chiamata a gpt-4o-mini con prompt corto per aggiungere una voce costa nell'ordine dei centesimi ogni mille messaggi: irrilevante. Il router serve perché una risposta in 50 ms è un'esperienza diversa da una in 2 secondi, e perché le funzioni banali non devono dipendere dalla disponibilità del servizio LLM.

Il risparmio economico reale arriva dall'evitare lo **schema dei tool nel prompt**, che è il vero peso: 3-4k token ripetuti a ogni turno. Vedi [05-ottimizzazioni.md](05-ottimizzazioni.md).

## WhatsApp — dove il modello economico si decide

Meta fattura per **conversazione** (finestra di 24 ore), con tariffa che varia per categoria e paese. Ordini di grandezza per l'Italia:

| Categoria | Costo indicativo per conversazione |
|---|---|
| Service (avviata dall'utente) | Spesso gratuita entro una soglia mensile |
| Utility (template: promemoria, notifiche) | €0,03-0,05 |
| Marketing | €0,08-0,12 |

Le notifiche proattive di questo prodotto ricadono in **Utility**.

### Lo scenario che va evitato

Un promemoria mattutino cade sempre fuori dalla finestra 24h → template a pagamento.

```
100 utenti × 1 digest mattutino × 30 giorni × €0,04 = €120/mese
```

E questo con **una sola** notifica al giorno. Con una notifica per ogni voce aggiunta alla lista condivisa, il conto esplode: una famiglia che aggiunge 10 articoli genera 10 notifiche verso ciascun altro membro.

### Mitigazioni obbligatorie prima di aprire WhatsApp

1. **Digest, non eventi.** Una notifica aggregata al giorno, non una per modifica.
2. **Notifiche proattive opt-in**, non attive per default.
3. **Sfruttare la finestra 24h.** Un utente che scrive al bot durante il giorno riapre la finestra: le notifiche successive sono gratuite. Un utente attivo costa quasi nulla, un utente passivo costa il template.
4. **Il digest quotidiano è l'unico template che vale la spesa.** Costa un template al giorno, ma se l'utente risponde riapre la finestra e rende gratuite tutte le notifiche successive della giornata — promemoria, avvisi di budget, modifiche alla lista. È l'investimento che ripaga; una notifica per singolo evento no.
5. **Differenziare per canale.** Su Telegram le notifiche sono libere e gratuite: nessun motivo per limitarle. La logica di notifica deve leggere `ChannelCapabilities.SupportsProactiveFree`.
6. **Nessun template per la lista condivisa fuori finestra.** Se B non ha scritto al bot da 24 ore, la modifica di A la vedrà alla prossima apertura. È accettabile per una lista della spesa.

Con queste mitigazioni, 100 utenti su WhatsApp stanno realisticamente sui €30-60/mese di template invece dei €120+ dello scenario naïf.

## Costi non-Azure

| Voce | Costo |
|---|---|
| Telegram | €0 |
| Numero telefonico per WhatsApp | €5-15/mese (SIM dati o numero VoIP compatibile) |
| Dominio | €10-20/anno |
| Google OAuth verification (scope sensitive) | €0, solo tempo |
| Microsoft publisher verification | €0 (richiede Partner Center account) |
| Google CASA assessment (**solo** per scope Gmail restricted) | **migliaia di €/anno** |
| Certificazione skill Amazon | €0 (integrazione scartata) |

L'ultima riga è il motivo per cui Gmail è in Fase 4 con un punto di domanda: quella cifra non si giustifica su un progetto personale a meno che l'email diventi il valore centrale del prodotto.

## Leve di ottimizzazione, in ordine di impatto

1. **Modello piccolo per default** — fattore ~16 sul costo LLM
2. **Prompt caching sullo schema dei tool** — riduzione significativa sui turni ripetuti
3. **Digest invece di notifiche per evento su WhatsApp** — fattore 5-10 sui template
4. **Router di intent** — porta a zero le operazioni frequenti (impatto maggiore su latenza che su costo)
5. **Cache in memoria dei token Key Vault** — pochi euro, ma banale da fare
6. **Auto-pause su SQL** — pochi euro, con costo in latenza

Le prime tre valgono più delle altre tre insieme.

## Soglia di sostenibilità

Se il prodotto restasse gratuito per un'utenza allargata, il punto di rottura arriva intorno ai **200-300 utenti su Telegram** (~€100/mese fra infrastruttura e LLM) o già ai **50-100 utenti su WhatsApp** con notifiche proattive.

Questo non è un problema della Fase 1 — è la ragione per cui il punto di decisione sta alla fine della Fase 1. Se la retention c'è, la domanda diventa monetizzazione o limitazione dell'utenza; se non c'è, il conto non è mai stato un problema.

## Piani a pagamento

Lo schema esiste già (`SubscriptionPlan`, per spazio non per utente — vedi [02-modello-dati.md](02-modello-dati.md#piano-di-abbonamento) per l'entità e la tabella dei livelli attuali), introdotto in anticipo apposta per non dover riscrivere `Space` quando arriverà davvero la fatturazione.

**L'enforcement non è ancora collegato.** Nessun punto del codice oggi impedisce di superare `MaxLinkedBots` o `MaxCallsPerDay` — l'unica protezione economica attiva è il rate limiting fisso per identità di canale (60 msg/ora, vedi [07-compliance.md](07-compliance.md)), indipendente dal piano. Restano da fare, quando si deciderà di attivarli:

- Un conteggio giornaliero delle chiamate per spazio (`Space`), con reset a mezzanotte — stesso pattern di idempotenza già usato per `Budget.LastAlertedFor`/`RecurringExpense.LastGeneratedFor`.
- Un controllo al momento del collegamento di una nuova identità di canale allo spazio, contro `MaxLinkedBots`.
- Un flusso di pagamento reale (Stripe o simile) e la UI in console per scegliere/cambiare piano.
- Le implicazioni di fatturazione/IVA che questo introduce, non coperte da [07-compliance.md](07-compliance.md) (che tratta solo GDPR).

Deliberatamente rimandato a dopo il punto di decisione di Fase 1: introdurlo prima significherebbe costruire fatturazione per un prodotto che non ha ancora dimostrato di essere usato.
