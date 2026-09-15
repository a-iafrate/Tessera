# 04 — Costi

> **Tutti i numeri sono ordini di grandezza a memoria, riferiti al listino noto a maggio 2026, regione West Europe, IVA esclusa.** I prezzi Azure e Azure OpenAI cambiano con regolarità. Prima di prendere decisioni, verificare su [Azure Pricing Calculator](https://azure.microsoft.com/pricing/calculator/).

## Riepilogo

| Scenario | Costo mensile |
|---|---|
| Fase 1 — MVP Telegram, ~10 utenti | **€25-40** |
| Fase 2 — con calendario, ~50 utenti | **€50-80** |
| Fase 3 — canali proattivi (email + push), ~100 utenti | **€55-85** |

Niente salto di costo alla Fase 3 (WhatsApp l'avrebbe prodotto, con i suoi template a pagamento — vedi [03-integrazioni.md](03-integrazioni.md), non perseguito per ragioni amministrative prima ancora che di costo). Email (ACS) e web push (nessun costo per notifica, solo l'infrastruttura VAPID già inclusa) restano voci marginali anche a 100 utenti.

## Fase 1 — MVP

| Voce | SKU | €/mese |
|---|---|---|
| Azure Web App | App Service B1 (always-on) | 12-15 |
| Azure SQL | Serverless, 0.5-1 vCore, auto-pause | 5-15 |
| Key Vault | Standard, a operazione | ~1 |
| Application Insights | Primi 5 GB/mese gratuiti | 0 |
| Blob Storage | Scontrini, fase 4 | 0 |
| Azure OpenAI | gpt-4o-mini, vedi sotto | 3-10 |
| Azure Communication Services | Email, reset password + digest quotidiano | <1-2 |
| Dominio | ~15/anno | ~1 |
| **Totale** | | **~25-40** |

Note:

- **Always-on non è opzionale.** Senza, l'app va in idle e il primo webhook dopo l'inattività va in timeout. Il tier Free non lo supporta: B1 è il minimo.
- **Azure SQL serverless con auto-pause** è la scelta economica giusta, con l'avvertenza che il risveglio dalla pausa costa alcuni secondi di latenza. Se il bot "sembra lento al mattino", è quello. L'alternativa è il tier Basic a costo fisso (~€5) senza auto-pause, adeguato per l'MVP.
- **Key Vault** si paga a operazione: mettere in cache i token in memoria con TTL, non rileggerli a ogni messaggio.
- **Service Bus: non incluso.** Nell'MVP la coda è in memoria. Aggiungerlo costa ~€10/mese (Basic) quando servirà.
- **Azure Communication Services** fattura per email inviata (pochi centesimi di dollaro per mille email alla tariffazione attuale, da verificare in fase di setup). Include ora anche il digest quotidiano via email (docs/13-piano-miglioramenti.md, C1) — al massimo un invio per utente al giorno, non uno per notifica (`EmailChannel.Capabilities.SupportsRealTimeNotifications = false`), quindi resta una voce marginale anche a qualche centinaio di utenti.

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
| Vocale via trascrizione (E1) | audio, non token testuali | ~€0,001-0,003 (millesimi al minuto) |

**Il fast path conta più per la latenza che per il costo.** Una chiamata a gpt-4o-mini con prompt corto per aggiungere una voce costa nell'ordine dei centesimi ogni mille messaggi: irrilevante. Il router serve perché una risposta in 50 ms è un'esperienza diversa da una in 2 secondi, e perché le funzioni banali non devono dipendere dalla disponibilità del servizio LLM.

Il risparmio economico reale arriva dall'evitare lo **schema dei tool nel prompt**, che è il vero peso: 3-4k token ripetuti a ogni turno. Vedi [05-ottimizzazioni.md](05-ottimizzazioni.md).

## WhatsApp — perché sarebbe stato caro anche a prescindere dal blocco amministrativo

Non perseguito per ragioni amministrative, non di costo (Business Verification Meta — vedi [03-integrazioni.md](03-integrazioni.md#whatsapp-cloud-api--non-perseguito)). Ma il modello economico l'avrebbe comunque reso il canale più caro del gruppo, ed è la ragione per cui questa sezione resta come nota storica.

Meta fattura per **conversazione** (finestra di 24 ore): un promemoria mattutino cade quasi sempre fuori finestra, quindi a **template a pagamento** (categoria Utility, ~€0,03-0,05/conversazione in Italia).

```
100 utenti × 1 digest mattutino × 30 giorni × €0,04 = €120/mese
```

Questo con **una sola** notifica al giorno — una per ogni voce aggiunta alla lista condivisa avrebbe fatto esplodere il conto. Le mitigazioni pensate per contenerlo — digest invece di notifiche per singolo evento, differenziazione per canale — sono diventate comunque funzionalità reali: [Fase 3](06-roadmap.md#fase-3--canali-proattivi-senza-burocrazia-2-settimane) (email + web push) le ha ereditate tramite `ChannelCapabilities.SupportsRealTimeNotifications`, senza però il costo per conversazione che le rendeva indispensabili in primo luogo.

## Costi non-Azure

| Voce | Costo |
|---|---|
| Telegram | €0 |
| Dominio | €10-20/anno |
| Google OAuth verification (scope sensitive) | €0, solo tempo |
| Microsoft publisher verification | €0 (richiede Partner Center account) |
| Google CASA assessment (**solo** per scope Gmail restricted) | **migliaia di €/anno** |
| Certificazione skill Amazon | €0 (integrazione scartata) |

L'ultima riga è il motivo per cui Gmail è in Fase 4 con un punto di domanda: quella cifra non si giustifica su un progetto personale a meno che l'email diventi il valore centrale del prodotto.

## Leve di ottimizzazione, in ordine di impatto

1. **Modello piccolo per default** — fattore ~16 sul costo LLM
2. **Prompt caching sullo schema dei tool** — riduzione significativa sui turni ripetuti
3. **Digest invece di notifiche per evento su email** — una sola azione schedulata al giorno per utente invece di un invio ACS per ogni evento (`ChannelCapabilities.SupportsRealTimeNotifications = false` per `EmailChannel`)
4. **Router di intent** — porta a zero le operazioni frequenti (impatto maggiore su latenza che su costo)
5. **Cache in memoria dei token Key Vault** — pochi euro, ma banale da fare
6. **Auto-pause su SQL** — pochi euro, con costo in latenza

Le prime due valgono più delle altre quattro insieme.

## Soglia di sostenibilità

Se il prodotto restasse gratuito per un'utenza allargata, il punto di rottura arriva intorno ai **200-300 utenti su Telegram** (~€100/mese fra infrastruttura e LLM) — soglia più alta di quanto stimato quando WhatsApp era ancora in piano, dato che email e web push non aggiungono un costo per notifica. Rende la monetizzazione un'opzione da valutare quando la retention lo giustifica, non una necessità imminente.

Questo non è un problema della Fase 1 — è la ragione per cui il punto di decisione sta alla fine della Fase 1. Se la retention c'è, la domanda diventa monetizzazione o limitazione dell'utenza; se non c'è, il conto non è mai stato un problema.

## Piani a pagamento

Lo schema esiste già (`SubscriptionPlan`, per spazio non per utente — vedi [02-modello-dati.md](02-modello-dati.md#piano-di-abbonamento) per l'entità e la tabella dei livelli attuali), introdotto in anticipo apposta per non dover riscrivere `Space` quando arriverà davvero la fatturazione.

**Enforcement collegato.** Cinque assi di valore — `MaxReceiptsPerMonth`, `MaxLinkedCalendars`, `HistoryMonths`, `AllowsExport`, `MaxSpacesOwned` — più `MaxCallsPerDay`/`MaxLinkedBots` come guardie anti-abuso non più vendute (D1, vedi [02-modello-dati.md](02-modello-dati.md#piano-di-abbonamento) per l'entità, i due piani attuali e ogni punto di enforcement). Il rate limiting fisso per identità di canale (60 msg/ora, vedi [07-compliance.md](07-compliance.md)) resta comunque attivo in parallelo, indipendente dal piano. Flusso di pagamento reale implementato con PayPal (sottoscrivi, cambia piano, annulla — tutti testati end-to-end in sandbox, [06-roadmap.md](06-roadmap.md)). Restano da fare:

- Le implicazioni di fatturazione/IVA che l'incasso introduce, non coperte da [07-compliance.md](07-compliance.md) (che tratta solo GDPR).
- Il passaggio da sandbox a live (solo configurazione, nessun codice — [08-setup-sviluppo.md](08-setup-sviluppo.md)).
- Le cifre finali dei piani, ancora un placeholder — vedi *Decisioni aperte* in [13-piano-miglioramenti.md](13-piano-miglioramenti.md).

Deliberatamente rimandato a dopo il punto di decisione di Fase 1: introdurlo prima significherebbe costruire fatturazione per un prodotto che non ha ancora dimostrato di essere usato.

### Provider di pagamento: PayPal, non Stripe

Decisione presa (non più "Stripe o simile"): **PayPal Subscriptions REST API**. Motivo principale — non tecnico, fiscale, specifico a un operatore in **regime forfettario** italiano come Acquariusoft:

- Le commissioni PayPal sono trattate come servizio finanziario esente IVA. Le commissioni Stripe arrivano invece da un fornitore con sede in Irlanda: ogni fattura commissioni triggera reverse charge, con autofattura elettronica (TD17) e F24 mensile — un adempimento ricorrente che PayPal evita quasi del tutto.
- Nessun obbligo di posizione VIES per le commissioni del gateway, non avendo acquisti intracomunitari soggetti a reverse charge da parte sua.
- Con solo due piani fissi (vedi [02-modello-dati.md](02-modello-dati.md#piano-di-abbonamento)) e nessuna necessità di scalare a logiche di fatturazione complesse, la superficie di integrazione resta comunque piccola.

**Non verificato da un commercialista in questa sede** — la lettura sopra viene da una consulenza esterna dell'utente, non da un parere fiscale raccolto qui. In particolare resta da confermare con chi segue la posizione forfettaria: l'interazione fra regime forfettario (niente IVA sulle vendite italiane) e registrazione OSS (necessaria solo superati i 10.000 € lordi annui di vendite verso privati in altri Paesi UE) — sotto soglia le vendite estere UE si trattano come vendite italiane, sopra soglia serve OSS a parte.

Dettagli del flusso tecnico (API, webhook, sandbox) in [03-integrazioni.md](03-integrazioni.md#paypal-subscriptions--pagamenti); schema dati proposto in [02-modello-dati.md](02-modello-dati.md#abbonamento-paypal-per-spazio).
