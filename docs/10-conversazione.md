# 10 — Conversazione: onboarding, undo, recupero dagli errori

## Perché questo documento

I documenti precedenti descrivono cosa il bot sa fare. Questo descrive cosa succede quando **non** funziona — che è la maggior parte dell'esperienza reale, e la parte che determina se qualcuno usa ancora il prodotto la settimana dopo.

Tre momenti pesano più di qualsiasi funzione aggiuntiva:

1. I primi due minuti dopo `/start`, dove si perde la maggioranza degli utenti
2. Il 30% di messaggi che il router non capisce ([05](05-ottimizzazioni.md))
3. Il momento in cui il bot fa la cosa sbagliata e l'utente deve rimediare

In chat non esiste un tasto indietro, non c'è una schermata da esplorare, non c'è un tooltip. L'unica affordance è il messaggio precedente. Questo cambia le regole rispetto a un'interfaccia grafica.

## Onboarding

### Il problema

Un'app mostra la sua struttura: vedi le tab, capisci cosa fa. Una chat mostra un cursore lampeggiante. L'utente che ha appena fatto `/start` non sa se può scrivere liberamente o se servono comandi, non sa cosa il bot capisce, e non sa perché dovrebbe usarlo invece delle note del telefono.

Il fallimento tipico non è un errore: è un `/start`, un messaggio di benvenuto, e nessun secondo messaggio mai.

### Regola: primo valore prima della configurazione

L'istinto è chiedere: nome, lingua, fuso, spazio, membri da invitare. Sbagliato. Ogni domanda prima del primo risultato utile è un punto di abbandono.

L'inversione: **il primo messaggio deve produrre qualcosa**, la configurazione arriva dopo o si deduce.

```
Utente: /start

Bot: Ciao! Sono Tessera. Gestisco liste, spese e promemoria — anche
     condivisi con altre persone.

     Provami subito: scrivi qualcosa come
     "aggiungi il latte alla lista"
     [ Aggiungi il latte ]  ← bottone che invia il testo per lui
```

Il bottone che invia il messaggio al posto dell'utente è il dettaglio che conta: elimina l'attrito di dover inventare cosa scrivere, e insegna la sintassi mostrandola.

Cosa **non** chiedere all'avvio:

| Dato | Come ottenerlo senza chiedere |
|---|---|
| Lingua | `Message.From.LanguageCode` come default (vedi [09](09-localizzazione.md)) |
| Nome | `From.FirstName` da Telegram |
| Fuso orario | Deduzione dalla lingua/paese come default, correggibile poi |
| Spazio | Creato automaticamente come "Personale" |
| Account console | Serve solo per il calendario, cioè in Fase 2 |

Il fuso dedotto dalla lingua è approssimativo (`it` → `Europe/Rome`) e va corretto alla prima interazione che dipende dall'ora: quando l'utente crea il primo promemoria, la conferma mostra l'orario e offre un bottone per cambiare fuso se è sbagliato. Chiederlo prima è una domanda a vuoto per la maggior parte degli utenti.

### La sequenza dei primi minuti

```
1. /start          → benvenuto + un solo invito ad agire
2. prima azione    → conferma + UN suggerimento di scoperta, non tre
3. seconda azione  → conferma + accenno alla condivisione
4. terza azione    → proposta di invitare qualcuno / collegare la console
```

La progressione conta: **una novità per volta.** Un messaggio di benvenuto che elenca sei capacità viene ignorato, e paradossalmente insegna meno di uno che ne mostra una.

```
Utente: aggiungi il latte

Bot: ✓ Latte aggiunto alla lista.

     Funziono anche con le spese — prova "ho speso 12 euro all'Esselunga".
```

Il suggerimento in coda va **retirato dopo poche volte**: superata la terza interazione, o quando l'utente ha già usato quella funzione, spariscono. Un bot che continua a spiegarsi diventa rumore. Serve un contatore per utente di suggerimenti mostrati per funzione.

### La condivisione va introdotta presto, ma non subito

La condivisione è la tesi del prodotto ([README](README.md)), quindi va scoperta — ma non al primo messaggio, quando l'utente non ha ancora capito a cosa serve il bot.

Il momento giusto è dopo la terza o quarta azione utile, ed è meglio se contestuale:

```
Bot: ✓ Pane aggiunto alla lista.

     Vuoi condividere questa lista con qualcuno? Chi la condivide
     vede le modifiche in tempo reale.
     [ Invita una persona ]  [ Più tardi ]
```

"Più tardi" deve essere una risposta accettata definitivamente: se l'utente lo tocca, non riproporre per settimane. Un invito ripetuto è la cosa che fa silenziare un bot.

### Nei gruppi l'onboarding è diverso

Quando il bot viene aggiunto a un gruppo, il pubblico è multiplo e la storia è già iniziata: qualcuno l'ha già usato in privato, gli altri no.

```
Bot: Ciao! Sono Tessera, l'assistente di Alessio per liste e spese condivise.

     Da qui potete aggiungere alla lista di casa scrivendo
     "/list aggiungi il latte".

     Nota: leggo solo i messaggi che iniziano con / o che mi menzionano.
```

L'ultima riga è importante per due ragioni: gestisce l'aspettativa (altrimenti qualcuno scrive liberamente e pensa che il bot sia rotto) ed è una dichiarazione di privacy sostanziale — vedi [07](07-compliance.md) sulla privacy mode.

## Undo

### Perché serve più che in un'app

In un'interfaccia grafica un errore è visibile e correggibile: vedi la voce sbagliata, la tocchi, la modifichi. In chat l'errore scorre via, e correggerlo richiede di formulare un'altra frase — che può sbagliare a sua volta.

Peggio: il router **sbaglierà per definizione**. Il fast path ha una copertura parziale e l'LLM interpreta. Un undo affidabile è quello che rende accettabile un router imperfetto, e quindi vale più di qualsiasi miglioramento marginale delle regex.

### Modello

L'ultima operazione reversibile per utente, con TTL breve:

```csharp
public class LastOperation
{
    public Guid UserId { get; set; }
    public Guid SpaceId { get; set; }
    public string OperationType { get; set; } = null!;   // "shopping.add", "expense.create"
    public string UndoPayloadJson { get; set; } = null!; // cosa serve per annullare
    public DateTimeOffset PerformedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }        // +10 minuti
    public bool IsUndone { get; set; }
}
```

Una sola operazione per utente, non uno stack. Un undo multiplo in chat è confusionario ("annulla" tre volte: cosa hai annullato?) e apre problemi di consistenza su risorse condivise.

L'undo è **per utente, non per spazio**: se tu aggiungi una voce e tua moglie ne aggiunge un'altra, "annulla" deve toccare la tua. Diversamente, in uno spazio attivo, l'undo diventa imprevedibile.

### Cosa è annullabile

| Operazione | Undo | Come |
|---|---|---|
| Aggiunta voce in lista | Sì | Cancellazione fisica |
| Spunta voce | Sì | Ripristino `IsChecked = false` |
| Registrazione spesa | Sì | Cancellazione |
| Creazione promemoria | Sì | Cancellazione |
| Svuotamento lista | Sì | Ripristino da payload — è l'undo che serve di più |
| Invito membro | No | Richiede rimozione esplicita, non è un errore di battitura |
| Modifica permessi | No | Azione deliberata dalla console |
| Evento di calendario (fase 2) | Parziale | Solo se creato dal bot e non ancora modificato altrove |

Lo svuotamento della lista è il caso in cui l'undo salva la giornata: il payload conserva tutte le voci cancellate. Senza, un `/list clear` accidentale è una perdita di dati.

### Superficie di attivazione

Tre strade, tutte necessarie:

- **Bottone inline** sulla conferma dell'operazione — la più usata, zero costo LLM
- **Comando** `/undo`
- **Frase naturale** ("annulla", "no aspetta", "undo") come intent L2 ad alta priorità

```
Bot: ✓ Latte aggiunto alla lista.
     [ ↩ Annulla ]
```

Il bottone scompare naturalmente quando il messaggio scorre via, che è il comportamento giusto: l'undo è per l'errore immediato, non per la revisione dello storico.

### Correzione, non solo annullamento

Più utile dell'undo puro: la correzione. `"no, 2 litri"` subito dopo un'aggiunta dovrebbe modificare la voce appena creata, non crearne una nuova.

Questo richiede che `LastOperation` sia consultabile dal router: un messaggio breve e correttivo entro pochi secondi da un'operazione va interpretato come riferito a quella. È il caso in cui passare a L3 con il contesto dell'ultima operazione nel prompt vale il costo.

Nota sulle risorse condivise: se nel frattempo un altro membro ha modificato la stessa voce, l'undo va rifiutato con un messaggio chiaro invece di sovrascrivere silenziosamente. Meglio "non posso annullare, Sara ha modificato la lista nel frattempo" che una modifica invisibile ai suoi occhi.

## Recupero dagli errori

### Il fallimento vero non è l'errore, è il vicolo cieco

`"Non ho capito"` è la risposta peggiore possibile: informa l'utente che ha sbagliato senza dirgli come fare, e non offre nessuna via d'uscita. Ripetuta due volte, chiude la conversazione per sempre.

Un messaggio di fallimento deve fare tre cose: dire cosa non ha funzionato, offrire un'interpretazione plausibile, e dare un'uscita.

```
Utente: metti a posto la roba di ieri

Bot: Non sono sicuro di aver capito. Intendevi una di queste?
     [ Vedere la lista ]  [ Vedere le spese di ieri ]  [ Aiuto ]
```

I bottoni non sono decorativi: sono la via d'uscita, e sono anche la fonte migliore di dati per migliorare il router. Se l'utente tocca "Vedere le spese di ieri", quella frase va nel corpus di test associata a quell'intent.

### Gradazione della risposta al fallimento

Non tutti i fallimenti sono uguali, e trattarli allo stesso modo è un errore:

| Situazione | Risposta |
|---|---|
| Intent chiaro, dato mancante | Chiedi solo il dato mancante: "Quanto hai speso?" |
| Intent ambiguo fra due | Bottoni con le due interpretazioni |
| Intent non riconosciuto | Suggerimento contestuale + `/help` |
| Permesso insufficiente | Dì **quale** spazio e **quale** permesso manca, non "non autorizzato" |
| Servizio LLM non disponibile | Fallback esplicito ai comandi: "Ho un problema tecnico, ma `/list` funziona" |
| Errore interno | Scuse brevi, nessun dettaglio tecnico, log con correlation id |

L'ultima riga del quinto caso è quella che giustifica il router deterministico oltre il risparmio di token: **se Azure OpenAI è giù, il bot resta parzialmente utile.** È una proprietà di resilienza, non un'ottimizzazione.

### Errori di permesso

In uno spazio condiviso con permessi granulari ([02](02-modello-dati.md)), "non hai i permessi" è inutile: l'utente non sa quale dei suoi spazi ha rifiutato, né cosa gli manca.

```
Bot: Non puoi aggiungere spese in "Amici tennis" — lì hai solo
     accesso alle disponibilità del calendario.

     Vuoi registrarla in "Casa"?
     [ Sì, in Casa ]  [ Annulla ]
```

Il messaggio nomina lo spazio, dice cosa manca, e propone l'alternativa plausibile. È anche una scorciatoia per la disambiguazione descritta in [02](02-modello-dati.md).

### Conferme: solo dove servono

Ogni conferma è attrito. La regola: **conferma le operazioni distruttive o costose, non le reversibili.**

| Operazione | Conferma |
|---|---|
| Aggiungi voce | No — c'è l'undo |
| Spunta voce | No |
| Registra spesa | No — c'è l'undo, ma rileggi l'importo interpretato |
| Svuota lista | **Sì** |
| Elimina spazio | **Sì**, con digitazione del nome |
| Crea promemoria | No, ma **rileggi data e ora** |
| Invia messaggio a terzi (fase 2) | **Sì, sempre** |

La rilettura non è una conferma: è un'informazione che l'utente può ignorare. "✓ Promemoria per giovedì 6 agosto alle 9:00" non richiede risposta, ma permette di accorgersi dell'errore. Sulle date e sugli importi è indispensabile — sono i due casi in cui un'interpretazione sbagliata passa inosservata e produce danno.

## Tono e forma dei messaggi

È una chat, non un'applicazione. Alcune regole che vale la pena fissare, perché la deriva è facile quando i messaggi li genera un LLM:

- **Brevi.** Una o due righe. La conferma di un'aggiunta è `✓ Latte aggiunto`, non un paragrafo.
- **Nessun markdown pesante.** Titoli e liste puntate in una chat sono fuori luogo. Le liste vere sono un caso a parte, ma vanno rese con formattazione minima.
- **Niente scuse ripetute.** Un errore, una scusa breve, avanti.
- **Emoji con parsimonia**, e solo funzionali: `✓` per la conferma, `⏰` per un promemoria. Non decorativi.
- **Mai rivelare la meccanica interna.** L'utente non deve sapere se ha risposto il fast path o il modello, né vedere nomi di intent o di tool.
- **Nessuna domanda di cortesia in coda.** "C'è altro che posso fare?" a ogni messaggio è rumore in una chat che resta aperta per sempre.

Queste regole vanno nel system prompt, e le risposte del fast path vanno scritte a mano nelle risorse `.resx` rispettandole. Un bot in cui le risposte deterministiche e quelle generate hanno tono diverso si percepisce subito come incoerente.

## Ricerca nello storico

Non è una funzione di recupero errori, ma appartiene a questo documento perché è ciò che rende la conversazione una memoria invece di un flusso.

```
"quando ho comprato il trapano?"
"quanto spendiamo di solito di benzina?"
"l'anno scorso a Natale quanto abbiamo speso?"
```

Sono query sui dati che già esistono, quasi gratuite da implementare, e crescono di valore col tempo — è quello che rende costoso abbandonare il prodotto dopo sei mesi. Nessuna app di liste conserva uno storico interrogabile in linguaggio naturale.

Implementazione: intent `history.query` che va a L3 (la varietà delle formulazioni è troppo alta per il pattern matching), il modello compone i parametri, la query è SQL. Il costo LLM è un turno; il risultato è un'aggregazione, non un dump di righe.

## Liste generiche

Il modello `ShoppingList` con `Name` già le permette senza codice nuovo: "da portare in vacanza", "regali di Natale", "film da vedere". Un comando in più e l'estensione è fatta.

Vale la pena farlo in Fase 1 proprio perché costa quasi nulla e amplia molto il numero di occasioni d'uso — e la frequenza d'uso è quello che si sta misurando nel punto di decisione ([06](06-roadmap.md)).

Attenzione a un solo dettaglio: con più liste per spazio serve la disambiguazione ("in quale lista?"), che segue la stessa logica di precedenza degli spazi. La lista di default per spazio risolve la quasi totalità dei casi.

## Cosa misurare

Metriche specifiche di questo documento, da aggiungere a quelle di [05](05-ottimizzazioni.md):

| Metrica | Cosa dice |
|---|---|
| Utenti che fanno `/start` e mai un secondo messaggio | Fallimento dell'onboarding — la metrica più brutale |
| Numero di azioni utili nella prima sessione | Se è 0 o 1, il primo invito ad agire non funziona |
| Tasso di `/undo` sul totale delle operazioni | Se supera il 5%, il router interpreta male sistematicamente |
| Frasi che portano a "non ho capito" | Va letto ogni settimana: è il corpus di miglioramento del router |
| Tap sui bottoni di disambiguazione | Ogni tap è una coppia (frase, intent) da aggiungere ai test |
| Giorni fra il primo uso e l'invito a un secondo membro | Se è alto, la condivisione è introdotta troppo tardi o troppo male |
| Utenti attivi al giorno 7 e 14 | La metrica del punto di decisione |

L'ultima colonna della quarta riga è il lavoro settimanale più utile del progetto: leggere le frasi che il bot non ha capito e trasformarle in test. Il router migliora osservando i log, non ragionando a tavolino.

## Sintesi: cosa va in Fase 1

- [ ] Onboarding progressivo con primo valore prima della configurazione
- [ ] Bottone che invia il testo di esempio al posto dell'utente
- [ ] Suggerimenti contestuali con contatore, che si ritirano
- [ ] Introduzione della condivisione dopo la terza azione, con "più tardi" definitivo
- [ ] Messaggio dedicato all'ingresso in un gruppo, con dichiarazione della privacy mode
- [ ] `LastOperation` + `/undo` + bottone inline + intent naturale
- [ ] Correzione contestuale ("no, 2 litri")
- [ ] Messaggi di fallimento graduati, sempre con via d'uscita a bottoni
- [ ] Errori di permesso che nominano spazio e permesso mancante
- [ ] Rilettura di date e importi interpretati
- [ ] Fallback ai comandi deterministici quando l'LLM non è disponibile
- [ ] Regole di tono nel system prompt e nelle risorse
- [ ] Liste generiche oltre la spesa
- [ ] Ricerca nello storico via L3
- [ ] Log delle frasi non capite, con revisione settimanale
