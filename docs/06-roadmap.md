# 06 — Roadmap

## Fase 0 — Fondamenta (~1 settimana)

Prima di qualunque funzionalità.

- [ ] Scheletro della soluzione, progetti e riferimenti (vedi [01](01-architettura.md))
- [ ] `Directory.Build.props`, `.editorconfig`, `global.json` — con `InvariantGlobalization = false`
- [ ] `CLAUDE.md` e `.github/copilot-instructions.md` versionati (vedi [08](08-setup-sviluppo.md))
- [ ] Schema EF Core: `User`, `Space`, `Membership`, `MembershipPermission`, `ChannelIdentity` (vedi [02](02-modello-dati.md))
- [ ] `User.PreferredCulture`, `TimeZoneId`, `DefaultSpaceId` nello schema (vedi [09](09-localizzazione.md))
- [ ] `IAccessPolicy` con test unitari sui permessi granulari
- [ ] `IChannel` + `ChannelCapabilities`
- [ ] `IIntentMatcher` con dimensione `Culture` dal primo giorno
- [ ] Risorse `.resx` in inglese e italiano, chiavi semantiche
- [ ] Eventi di notifica strutturati (non stringhe pre-composte)
- [ ] Web App su Azure, dominio, certificato, Key Vault con Managed Identity
- [ ] CI/CD GitHub Actions → App Service

**Nulla di visibile all'utente, e va bene così.** Ci sono tre cose non rifattorizzabili a costo basso, e stanno tutte qui:

1. **Lo schema di condivisione.** Partire da `Utente → Lista` costa una riscrittura di ogni query e di ogni controllo di autorizzazione.
2. **Le notifiche come eventi strutturati.** Se nascono come stringhe già composte, renderle per-destinatario dopo significa riscrivere il sistema di notifica.
3. **I nomi canonici dei comandi Telegram.** Rinominarli dopo confonde chi li ha imparati.

Anche se in Fase 1 ogni spazio ha un solo membro e si parla solo italiano, i modelli sono già quelli definitivi.

## Fase 1 — MVP Telegram + console (~9-10 settimane)

Zero OAuth. Tutto vive nel database.

### Bot

- [ ] Webhook con validazione secret token e deduplica
- [ ] Coda in memoria + `BackgroundService`
- [ ] Router L1/L2/L3 con corpus di test iniziale, matcher IT e EN
- [ ] Cultura impostata esplicitamente nel worker per ogni messaggio
- [ ] Lista della spesa: aggiungi, spunta, rimuovi, mostra, svuota
- [ ] Inline keyboard per spuntare le voci
- [ ] Spese: registra, categoria, aggregazione mensile e per categoria
- [ ] Categorizzazione a basso attrito: mappa merchant appresa per spazio (vedi [02](02-modello-dati.md))
- [ ] Parsing importi con la cultura dell'utente, conferma nei casi ambigui
- [ ] **Promemoria**: creazione, ricorrenze semplici, completamento, assegnazione a un membro
- [ ] Parsing date in linguaggio naturale via L3, con rilettura in conferma (vedi [05](05-ottimizzazioni.md))
- [ ] **Spese ricorrenti** con auto-registrazione o promemoria
- [ ] **Budget per categoria** con avviso alla soglia
- [ ] **Digest quotidiano**: promemoria di oggi, cosa manca in lista, stato del budget
- [ ] `IScheduledJob` + worker temporizzato per promemoria, digest, ricorrenze
- [ ] Comandi nativi: `/list`, `/expense`, `/remind`, `/month`, `/link`, `/language`, `/help` + alias italiani
- [ ] Descrizioni dei comandi localizzate via `setMyCommands` con `language_code`
- [ ] Notifica agli altri membri dello spazio, resa nella lingua di ciascun destinatario
- [ ] Soppressione della notifica nel canale dove l'azione è già visibile (vedi [09](09-localizzazione.md))
- [ ] Ciclo di vita del gruppo: `migrate_to_chat_id`, bot rimosso, bot aggiunto (vedi [03](03-integrazioni.md))
- [ ] `/link` nel gruppo come rimedio manuale se l'associazione si perde
- [ ] Disambiguazione dello spazio in chat privata (catena di precedenza, vedi [02](02-modello-dati.md))

### Conversazione (vedi [10](10-conversazione.md))

Pesa più di qualsiasi funzione aggiuntiva: è ciò che determina se qualcuno usa ancora il bot la settimana dopo.

- [ ] Onboarding progressivo: primo valore prima della configurazione, un suggerimento per volta
- [ ] `OnboardingHint` — i suggerimenti si ritirano, "più tardi" è definitivo
- [ ] Introduzione della condivisione dopo la terza azione utile
- [ ] Messaggio di ingresso in un gruppo, con dichiarazione della privacy mode
- [ ] `LastOperation` + `/undo` + bottone inline + intent naturale ("annulla")
- [ ] Correzione contestuale ("no, 2 litri" subito dopo un'aggiunta)
- [ ] Messaggi di fallimento graduati, sempre con via d'uscita a bottoni
- [ ] Errori di permesso che nominano spazio e permesso mancante
- [ ] Rilettura di date e importi interpretati nella conferma
- [ ] Fallback ai comandi deterministici quando l'LLM non è disponibile
- [ ] Regole di tono nel system prompt e nelle risorse `.resx`
- [ ] **Liste generiche** oltre la spesa (vacanza, regali, film) — costo quasi nullo
- [ ] **Ricerca nello storico** via L3 ("quando ho comprato il trapano?")
- [ ] Log delle frasi non capite, con revisione settimanale

### Console web

- [ ] Blazor Web App, render server interattivo
- [ ] Registrazione e login con **ASP.NET Core Identity** (email/password); provider social (Google, Microsoft) aggiunti in fase successiva senza ridisegno — vedi [01-architettura.md](01-architettura.md)
- [ ] Creazione e gestione spazi
- [ ] Invito membri con preset di permessi
- [ ] Ruolo `Admin` **per spazio**, non globale: nessun amministratore di sistema che veda i dati altrui
- [ ] Uscita e rimozione da uno spazio, con trasferimento del ruolo Admin (vedi [02](02-modello-dati.md))
- [ ] `MembershipArchive` con snapshot del nome, e `ResolveActorName` come unico punto di resa
- [ ] Sezione "Ex membri" nella pagina dello spazio, con data di uscita
- [ ] Cancellazione account: pseudonimizzazione dei riferimenti condivisi, export offerto nello stesso flusso
- [ ] Collegamento Telegram via `LinkToken` e deep link
- [ ] Selettore di lingua e fuso orario nel profilo (scrive `User.PreferredCulture`)
- [ ] Scelta dello spazio di default
- [ ] Console localizzata IT/EN
- [ ] **Homepage pubblica, privacy policy, termini** — prerequisito per la Fase 2
- [ ] **Privacy policy tradotta in tutte le lingue dichiarate come supportate**

### In parallelo, da avviare subito

- [ ] Avviare la **OAuth verification Google** (2-6 settimane di coda: matura mentre si sviluppa)
- [ ] Avviare la **publisher verification Microsoft** (giorni)

Avviarle in Fase 1 è la scelta che fa risparmiare più tempo di tutto il resto del progetto.

### Uso reale

- [ ] Usarlo in famiglia per almeno due settimane
- [ ] Coinvolgere 5-10 persone esterne
- [ ] Application Insights: retention giorno 7 e giorno 14, distribuzione L1/L2/L3, tasso di "non ho capito"

## ⬛ Punto di decisione

**Alla fine della Fase 1, prima di scrivere una riga di Fase 2.**

Domanda: dopo la seconda settimana, la lista condivisa viene ancora usata?

| Esito | Azione |
|---|---|
| Sì, uso quotidiano | Procedere in Fase 2 |
| Uso sporadico | Capire perché prima di aggiungere superficie. Attrito nel linking? Router troppo rigido? La lista della spesa non era il bisogno? |
| Abbandonato | Fermarsi. Né il calendario né WhatsApp né Gmail salvano un prodotto che nessuno apre. |

Il costo di arrivare qui è ~10 settimane e ~€30/mese, coperti dal credito MVP. È un investimento proporzionato per rispondere alla domanda più importante del progetto.

Il tasso di "non ho capito" è il secondo numero da guardare: se è alto, il problema è il router, non il prodotto — e si aggiusta.

Se l'esito è "sì, uso quotidiano", la domanda successiva è monetizzazione o limitazione dell'utenza (vedi [04-costi.md](04-costi.md#soglia-di-sostenibilità)). Lo schema dei piani (`SubscriptionPlan`, per spazio) esiste già in anticipo — vedi [02-modello-dati.md](02-modello-dati.md#piano-di-abbonamento) — ma l'enforcement dei limiti e il flusso di pagamento restano deliberatamente fuori dalla Fase 1.

## Fase 2 — Calendario (~5-6 settimane)

Le verification avviate in Fase 1 dovrebbero essere concluse o vicine.

- [ ] Flusso OAuth Microsoft e collegamento account
- [ ] `ICalendarProvider` con implementazione Graph
- [ ] **Enumerazione dei calendari** (`calendarList` / `/me/calendars`) → `ExternalCalendar`
- [ ] Default: solo il calendario primario abilitato, gli altri opt-in dalla console
- [ ] **Mappatura calendario → spazio** con livello per calendario (`CalendarSpaceMapping`)
- [ ] Calcolo del livello effettivo come minimo fra provider, mappatura e membership
- [ ] `IsDefaultWriteTarget` per decidere dove creare gli eventi
- [ ] **Deduplica su `iCalUID`** per il calendario condiviso collegato da più membri
- [ ] Refresh periodico della lista calendari (accessRole e condivisioni cambiano lato provider)
- [ ] Flusso OAuth Google (dipende dalla verification conclusa)
- [ ] Implementazione Google Calendar
- [ ] Storage dei refresh token in Key Vault, cifrati (vedi [07](07-compliance.md))
- [ ] Lettura eventi: "che ho domani", "quando è la riunione con Marco"
- [ ] `AccessLevel.Availability` via `freebusy.query` / `getSchedule`
- [ ] Disponibilità incrociate: "quando siamo liberi io e Sara giovedì?"
- [ ] Creazione e spostamento eventi
- [ ] Promemoria proattivi (su Telegram, gratuiti)
- [ ] Gestione timezone corretta con IANA ID

**Microsoft prima di Google**: ciclo di feedback più corto, valida l'astrazione mentre la review Google è in coda.

La disponibilità incrociata è la funzione più difendibile del prodotto: risolve un problema reale ("quando ci vediamo?") che nessuna app di calendario risolve bene fra persone diverse.

## Fase 3 — WhatsApp (~3-4 settimane, molte di attesa)

**Solo con retention confermata su Telegram.**

- [ ] Meta Business Account e business verification
- [ ] Numero dedicato e WABA
- [ ] Webhook con validazione HMAC
- [ ] `WhatsAppChannel : IChannel` con `SupportsGroups = false`
- [ ] Linking via codice a 6 cifre (nessun deep link con payload)
- [ ] Lista condivisa come N conversazioni sullo stesso spazio
- [ ] Template approvati per le notifiche fuori finestra 24h
- [ ] **Logica di notifica differenziata per canale**: digest su WhatsApp, eventi su Telegram
- [ ] Monitoraggio del costo per utente sui template

Il layer di astrazione scritto in Fase 0 dovrebbe rendere questa fase un lavoro di adattamento, non di riscrittura. Se richiede di toccare la pipeline, l'astrazione era sbagliata.

Qui la domanda vera è economica, non tecnica: il costo per utente dei template regge? Vedi [04-costi.md](04-costi.md).

## Fase 4 — Estensioni

Nessuna urgenza, nessun ordine obbligato. In ordine di rapporto valore/sforzo:

- [ ] **Scontrini via vision** — killer feature per le spese. Costo reale ma accettabile (centesimi per scontrino). Richiede Blob Storage e gestione delle immagini.
- [ ] **Scontrino → spunta la lista + registra la spesa** — un gesto, due sistemi. È la dimostrazione della tesi dell'aggregazione, e nessun concorrente singolo può farlo.
- [ ] **Archivio garanzie** — "quando ho comprato la lavatrice?". Ricade gratis dagli scontrini già archiviati, e nessuna app di liste lo fa.
- [ ] **Calendario → lista** — "sabato cena con i Rossi" fa proporre di aggiungere alla lista prima del weekend.
- [ ] **Storico prezzi** — "il caffè costa il 15% in più di sei mesi fa". Richiede la normalizzazione dei nomi prodotto, che è lavoro sporco.
- [ ] **Ricette e suggerimenti dalla lista** — buon caso d'uso LLM, costo contenuto. Attenzione a non scivolare nel meal planning, che è un prodotto a sé.
- [ ] **Documenti** — OneDrive e Google Drive hanno scope più clementi di Gmail. Da valutare.
- [ ] **Email** ⚠️ — scope Gmail **restricted**, security assessment CASA annuale nell'ordine delle migliaia di euro. Microsoft Graph per la posta è più accessibile. Da fare **solo** se l'email diventa il valore centrale del prodotto, non come "feature in più".
- [ ] **Service Bus e più istanze** — quando il volume lo richiede, non prima.

**Criterio per ammettere una funzione in Fase 4:** deve essere una **connessione fra due funzioni esistenti**, non una voce autonoma in elenco. Le prime quattro voci lo sono; la tesi della suite rende razionale ogni aggiunta, ed è il meccanismo con cui i progetti non arrivano in produzione.

### Non-obiettivi dichiarati

| Funzione | Perché no |
|---|---|
| Divisione delle spese / debiti fra membri | Dominio a sé (quote, saldi, pareggi). Territorio di Splitwise e Tricount, confronto diretto e sfavorevole. Vedi [02](02-modello-dati.md) |
| Meal planning completo | Richiede ricette, porzioni, preferenze alimentari. Competizione diversa, pozzo senza fondo |
| Task di casa con turni e assegnazioni | Retention notoriamente bassa, tocca dinamiche familiari delicate, non connette nulla di esistente |
| Sync con la lista Alexa | Strutturalmente impossibile, API deprecate. Vedi [03](03-integrazioni.md) |

## Fase 5 — Ipotesi remote

- Custom skill Alexa con invocation name — solo se emerge che l'Echo è il punto d'ingresso principale dell'utenza. Il sync bidirezionale resta impossibile, vedi [03-integrazioni.md](03-integrazioni.md).
- Modello locale per la classificazione degli intent — sperimentazione interessante, ma App Service non ha GPU.
- App mobile — se si arriva qui, il prodotto ha funzionato e la conversazione è un'altra.

## Riepilogo tempi

| Fase | Durata sviluppo | Attese esterne |
|---|---|---|
| 0 — Fondamenta | 1 settimana | — |
| 1 — MVP + console | 9-10 settimane | verification avviate in parallelo |
| **Punto di decisione** | — | — |
| 2 — Calendario | 5-6 settimane | Google 2-6 settimane (già maturate) |
| 3 — WhatsApp | 3-4 settimane | business verification, variabile |
| 4 — Estensioni | aperta | — |

Da zero a calendario funzionante: **~16-17 settimane** di sviluppo, se le verification sono partite al momento giusto. Se partono in Fase 2, si aggiungono 4-6 settimane di attesa a vuoto.

## Regole trasversali

1. **Lo schema di condivisione va bene dal primo giorno.** Tutto il resto si rifattorizza.
2. **L'infrastruttura di localizzazione va in Fase 0-1.** Aggiungere una lingua dopo è lavoro meccanico; recuperare le notifiche come eventi strutturati o rinominare i comandi non lo è.
3. **Le verification si avviano appena possibile.** Sono tempo di calendario, non tempo di lavoro.
4. **Ogni fase deve produrre qualcosa di usabile.** Se una fase finisce senza che qualcuno la usi, era troppo lunga.
5. **Il punto di decisione è vincolante.** Serve a evitare di costruire per mesi qualcosa che nessuno vuole — ed è la ragione per cui la Fase 1 è deliberatamente povera di funzioni.
