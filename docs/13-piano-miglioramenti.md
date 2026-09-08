# 13 — Piano di miglioramento

Piano operativo derivato dall'analisi di settembre 2026, quando Fase 0-1-2 e buona parte della
Fase 4 erano completate. Non sostituisce [06-roadmap.md](06-roadmap.md): la roadmap dice *cosa
costruire e in che fase*, questo documento dice *cosa sistemare e migliorare in ciò che esiste
già*, con abbastanza dettaglio da poterlo riprendere a distanza di settimane.

## Come usare questo documento

Ogni voce ha la stessa struttura:

- **Dove** — file e simboli da toccare
- **Perché** — la ragione, con il rimando al documento che la stabilisce
- **Cosa** — l'intervento
- **Fatto quando** — il criterio di accettazione, verificabile

I lotti sono indipendenti fra loro salvo dipendenze dichiarate: ognuno è pensato come un commit
o una PR a sé. L'ordine consigliato sta in fondo, non nella numerazione.

Vincoli che valgono per ogni voce e non si ripetono in ognuna: testi utente solo via
`IStringLocalizer` con chiave inglese in `Messages.resx` più la traduzione in `Messages.it.resx`
([09-localizzazione.md](09-localizzazione.md)); nessun esadecimale nei componenti, solo
`var(--token)` da `tokens.css` ([12-stile-sito.md](12-stile-sito.md)); nessuna violazione delle
15 regole rigide in `CLAUDE.md`.

---

## Lotto A — Gap fra documentazione e codice

Non sono idee nuove: sono decisioni già scritte in `docs/` che il codice non implementa.
`CLAUDE.md` chiede di segnalare le divergenze anziché seguirne silenziosamente una delle due.
**È il lotto con il miglior rapporto impatto/sforzo dell'intero piano.**

### A1 — Filtrare lo schema dei tool per spazio ✅

- [x] **Dove**: `Tessera.Ai/Llm/LlmTools.cs`, chiamato da `LlmFallbackClient.cs:64`
- **Perché**: [05-ottimizzazioni.md](05-ottimizzazioni.md#schema-dei-tool-per-contesto) prescrive
  `toolRegistry.ForSpace(space, membership.Permissions)` e quantifica il guadagno: "da ~4k a ~800
  token per turno, fattore 5 sul costo del percorso L3". Oggi `LlmTools.Build` restituisce sempre
  l'insieme completo, inclusi i cinque tool di calendario mandati anche a spazi senza calendario
  collegato e i tool di spesa mandati a chi ha permesso `Read`.
- **Cosa**: introdurre `LlmToolRegistry.ForSpace(spaceId, permissions, hasLinkedCalendar)` che
  compone l'array partendo dai `ResourceKind` su cui il membro ha almeno `Read` e dalla presenza
  di un `ExternalCalendar` mappato sullo spazio. Il risultato va in cache per spazio con TTL 1 h
  (tabella cache di `05`) — dipende quindi da **A2**, o si accetta di ricalcolarlo ogni turno
  (costo trascurabile rispetto ai token risparmiati: non è un motivo per rinviare A1).
- **Fatto quando**: uno spazio di sola lista della spesa produce una richiesta con i soli tool di
  shopping, verificato su `L3TokensTotal` in Application Insights prima/dopo su una frase campione.
- **Fatto**: ogni tool porta ora `(ResourceKind, AccessLevel)` in `LlmTools.AllTools`;
  `LlmTools.Build` filtra per il livello effettivo del membro chiamante (calcolato in
  `MessageProcessor.BuildAccessByResource`, `Admin` per l'owner) e per la presenza di almeno una
  `CalendarSpaceMapping` nello spazio. Filtra anche per **livello**, non solo per risorsa
  accessibile — `record_expense` non compare più a chi ha solo `Read` su Expenses, un caso più
  stretto di quanto il testo originale del lotto descrivesse. **Deviazione dal testo del lotto**:
  nessuna cache separata per lo schema filtrato (l'`CacheTtl.ToolSchema` previsto in A2 non è
  stato creato) — i permessi sono per **membro**, non per spazio, quindi una cache chiave-per-
  spazio avrebbe rischiato di offrire per una finestra di TTL i tool di scrittura a un membro con
  solo `Read` che condivide lo stesso spazio con uno che ha `Write`. Costruire l'elenco filtrato è
  comunque a costo trascurabile (solo confronti su una lista fissa, nessuna query, nessuna
  serializzazione), quindi la cache non era necessaria per il guadagno che A1 cercava.

### A2 — Cache in memoria ✅

- [x] **Dove**: nuovo servizio in `Tessera.Data`, consumato da `ChannelIdentityRepository`,
  `AccessPolicy`/`MembershipRepository`, `KeyVaultTokenVault`, `ExpenseService.GetCategoriesAsync`
- **Perché**: la tabella "Cache: cosa e per quanto" di
  [05-ottimizzazioni.md](05-ottimizzazioni.md#cache-cosa-e-per-quanto) non è implementata: non
  esiste un solo `IMemoryCache` nella soluzione. Conseguenze misurabili: una operazione Key Vault
  a pagamento per ogni operazione di calendario ([04-costi.md](04-costi.md) — "mettere in cache i
  token in memoria con TTL, non rileggerli a ogni messaggio") e 2-3 query per messaggio per
  risolvere identità e permessi.
- **Cosa**: `AddMemoryCache()` in `Program.cs` e i cinque TTL della tabella —
  `ChannelIdentity → User` 15 min, permessi 5 min, token OAuth fino a scadenza − 5 min, categorie
  1 h, schema dei tool 1 h. I permessi vanno invalidati esplicitamente dalla console quando una
  membership cambia (`SpaceService`, `InviteService`), non solo per scadenza.
- **Attenzione**: [07-compliance.md](07-compliance.md#cache-in-memoria-con-attenzione) vincola la
  cache dei token: in memoria, mai su disco, e va svuotata allo scollegamento dell'account.
- **Fatto quando**: due messaggi consecutivi dello stesso utente producono una sola lettura Key
  Vault e una sola risoluzione di membership, verificato nei log in debug.
- **Fatto**: `Tessera.Data/Caching/{CacheKeys,CacheTtl}.cs` centralizzano chiavi e TTL.
  `ChannelIdentityRepository.ResolveUserAsync` (15 min), `MembershipRepository.FindAsync`
  (5 min, con `Invalidate` statico chiamato da `SpaceService` a ogni scrittura su `Membership`/
  `MembershipPermissions`, da `InviteService.ConsumeAsync` e da
  `AccountDeletionService` — quest'ultimo perché promuove un successore mutando `IsOwner`
  direttamente sul `DbContext`, bypassando `SpaceService.TransferOwnershipAsync`) ed
  `ExpenseService.GetCategoriesAsync` (1 h) sono in cache. `LinkedAccountService.GetValidAccessTokenAsync`
  cachea l'**access token** (non il refresh token, che resta solo in Key Vault, regola 4) fino a
  `scadenza − 5 minuti`, invalidata da `UnlinkAsync` — prima di questo intervento la chiamata
  faceva una lettura Key Vault **e** uno scambio col provider a ogni singolo messaggio che
  toccava il calendario, indipendentemente da quanto l'access token precedente fosse ancora
  valido: era il punto peggiore, più di quanto il testo del lotto lasciasse intendere.
  `IMemoryCache` registrato una volta in `Program.cs` (Singleton, condiviso fra tutti i servizi
  Scoped che lo iniettano).

### A3 — Storico conversazionale in L3

- [ ] **Dove**: `Tessera.Ai/Llm/LlmFallbackClient.TryCompleteAsync`, `LlmContext`,
  `ConversationState.StateJson`
- **Perché**: [05-ottimizzazioni.md](05-ottimizzazioni.md#storico-limitato) prescrive "ultimi 6-8
  turni, o ancora meglio uno stato conversazionale strutturato". Oggi il prompt è
  `[system, context, user]`: l'unica memoria è `RecentAction`. Il risultato è che "quanto ho speso
  a gennaio?" seguito da "e a febbraio?" non funziona — ed è il fallimento conversazionale più
  frequente in assoluto, quello che [10-conversazione.md](10-conversazione.md) considera
  determinante per la retention.
- **Cosa**: aggiungere a `ConversationState.StateJson` una coda degli ultimi 4 scambi
  (testo utente + intento risolto, non la risposta integrale), TTL 30 min come il resto dello
  stato, e inserirla nella **coda variabile** del prompt — dopo il context message, mai nel
  system prompt, o si invalida il prefisso cacheable.
- **Fatto quando**: la sequenza "quanto ho speso a gennaio" → "e a febbraio" risponde su febbraio,
  e un test sul corpus del router copre il caso.

### A4 — Messaggio perso al riavvio

- [ ] **Dove**: `Tessera.Web/Endpoints/TelegramUpdateIngestor.cs:37-51`,
  `Tessera.Web/Services/MessageProcessor.cs`, `ProcessedMessage`
- **Perché**: la riga di deduplica viene committata **prima** dell'enqueue nella coda in memoria.
  Al recycle dell'App Service — cioè a ogni deploy — un messaggio in coda è perso, **e** il retry
  di Telegram viene scartato dalla deduplica perché la riga risulta già presente. Nessun errore,
  nessuna traccia. `CLAUDE.md` regola 6 impone la deduplica su `ProviderMessageId`, non che sia
  irreversibile; [01-architettura.md](01-architettura.md#vincolo-noto-coda-in-memoria) dichiara il
  limite della coda ma non questa interazione con la deduplica.
- **Cosa**: `ProcessedMessage.CompletedAt` nullable. L'ingestor scrive la riga con `CompletedAt =
  null`, `MessageProcessor` la valorizza a lavorazione conclusa, la deduplica scarta solo le righe
  completate, e all'avvio un passo di recupero rimette in coda i pendenti più vecchi di un minuto.
  Nessun Service Bus: resta un rinvio corretto
  ([05](05-ottimizzazioni.md#cosa-non-ottimizzare-ancora)).
- **Fatto quando**: un riavvio con messaggi in coda li rielabora all'avvio senza duplicarli.

### A5 — Health check e pulizia

- [ ] **Dove**: `Program.cs`, nuovo `IScheduledJob`
- **Perché**: non esiste un endpoint di salute (App Service non ha modo di distinguere "vivo" da
  "risponde ma la coda è bloccata"), e `ProcessedMessages` cresce senza limite.
- **Cosa**: `MapHealthChecks("/health")` con un check su DB e uno sull'ultima elaborazione
  riuscita; `ProcessedMessagePurgeJob` che elimina le righe completate oltre i 7 giorni.
- **Fatto quando**: `/health` risponde e il job compare nei log dello scheduler.

### A6 — Metriche mancanti ✅

- [x] **Dove**: `LlmFallbackClient.TrackTurn`
- **Perché**: [05-ottimizzazioni.md](05-ottimizzazioni.md#cosa-misurare-da-subito) chiede i token
  per turno p50/p95 — c'è `L3TokensTotal`, manca lo split input/output e soprattutto i **cached
  token**, che sono l'unico modo di sapere se il prompt caching sta davvero funzionando. Senza,
  A1 non è misurabile.
- **Cosa**: tracciare `L3TokensInput`, `L3TokensOutput`, `L3TokensCached` dalla `ChatTokenUsage`.
- **Fatto quando**: il rapporto cached/input è visibile in Application Insights.
- **Fatto**: le tre metriche sono tracciate da `usage.InputTokenCount`, `.OutputTokenCount` e
  `.InputTokenDetails.CachedTokenCount` (SDK `OpenAI` 2.1.0), accanto a `L3TokensTotal` già
  esistente.

---

## Lotto B — Usabilità della console

Rilievi da lettura del codice e dagli screenshot in `tmp-shots/`. Diversi sono violazioni di
regole già scritte in [12-stile-sito.md](12-stile-sito.md), non opinioni estetiche.

### B1 — Il saluto mostra l'indirizzo email

- [ ] **Dove**: `Components/Pages/Home.razor:33`
- **Perché**: `context.User.Identity?.Name` per ASP.NET Core Identity è lo username, cioè l'email.
  La dashboard accoglie l'utente con "Welcome back, mario.rossi@example.com". `User.DisplayName`
  esiste, è già popolato dalle claim Google, ed è già letto da `MainLayout.razor` per la navbar —
  [06-roadmap.md](06-roadmap.md#login-social-e-profilo-utente) lo dà per fatto in console.
- **Cosa**: usare `DisplayName` con fallback alla parte locale dell'email, non all'indirizzo intero.
- **Fatto quando**: un account senza `DisplayName` vede "Welcome back, mario", non l'email.

### B2 — La dark mode è definita ma irraggiungibile

- [ ] **Dove**: `wwwroot/css/tokens.css:51`, `MainLayout.razor`
- **Perché**: `[data-theme="dark"]` è definito con l'intera palette scura, ma nessuna regola
  `@media (prefers-color-scheme: dark)` la attiva e nessun toggle imposta l'attributo. Metà del
  lavoro di palette è scritto e inerte. [12-stile-sito.md](12-stile-sito.md#accessibilità): "dark
  mode segue `prefers-color-scheme` di default, con toggle manuale in console che sovrascrive".
- **Cosa**: un blocco `@media (prefers-color-scheme: dark)` che ripete le assegnazioni della
  variante scura quando `data-theme` non è forzato, più un toggle a tre stati (auto / chiaro /
  scuro) in `/settings` persistito in `localStorage` e applicato prima del primo render per
  evitare il lampo di tema chiaro.
- **Fatto quando**: un sistema in dark mode apre la console già scura, e il toggle la sovrascrive
  attraverso una navigazione e un reload.

### B3 — La riga della lista della spesa

È la pagina a uso più frequente della console e la peggiore delle interazioni. Quattro problemi
distinti, uno stesso intervento.

- [ ] **Dove**: `Components/Pages/ShoppingList.razor` (righe 60-88), `wwwroot/css/base.css:518-553`
- **Perché**:
  1. **`isBusy` blocca l'intera lista.** Ogni bottone di ogni riga è `disabled="@isBusy"`: spuntare
     una voce congela tutte le altre per la durata del round trip. Chi fa la spesa spunta 15
     articoli di fila — [05-ottimizzazioni.md](05-ottimizzazioni.md#l1--comandi-e-callback)
     descrive esattamente questo scenario come il percorso da rendere istantaneo.
  2. **La gerarchia visiva è invertita.** Nello screenshot `02-shoppinglist-mobile.png` la pagina è
     dominata da tre bottoni rossi ("Remove", "Remove", "Clear list") mentre l'azione principale
     ("Done") è un secondario pallido. [12-stile-sito.md](12-stile-sito.md#bottoni) riserva
     `--danger` alle azioni distruttive; qui è il colore più presente sullo schermo, e "Done" e
     "Remove" sono adiacenti, di pari dimensione, senza separazione.
  3. **Densità.** Ogni voce è una card: due articoli riempiono uno schermo di telefono.
  4. **Nessuna separazione fra spuntato e non spuntato**, nessun conteggio.
- **Cosa**: riga densa con checkbox nativa (o riga interamente cliccabile, target ≥ 44 px) come
  azione primaria; "rimuovi" come icona discreta allineata a destra, non un bottone `--danger` a
  piena larghezza; stato busy **per riga** anziché globale, con aggiornamento ottimistico e
  rollback in caso di errore; voci spuntate raccolte in fondo sotto un'intestazione "già preso
  (n)"; conteggio in cima. "Svuota la lista" resta `--danger` e resta con conferma (già presente,
  `ShoppingList.razor:204`), ma spostato fuori dal flusso della lista.
- **Fatto quando**: spuntare tre voci di seguito su telefono non blocca la lista, e nessun bottone
  `--danger` compare nelle righe.

### B4 — Variabile CSS inesistente

- [ ] **Dove**: `Components/Pages/ShoppingList.razor:68`
- **Perché**: usa `var(--text-soft)`, che non esiste in `tokens.css` — `.text-soft` è una *classe*
  (`base.css:377`), il token si chiama `--ink-soft`. Le voci spuntate ottengono la barratura ma non
  l'attenuazione. È l'unico riferimento a un token inesistente in tutto il progetto (verificato
  incrociando definizioni e usi).
- **Cosa**: `var(--ink-soft)`, e la regola va nel CSS di componente, non in uno `style` inline.
- **Fatto quando**: una voce spuntata è visibilmente attenuata oltre che barrata.

### B5 — Landmark e navigazione da tastiera

- [ ] **Dove**: `Components/Layout/MainLayout.razor`
- **Perché**: `@Body` non è dentro un `<main>`, non c'è `<header>`, non c'è skip link. Chi usa uno
  screen reader o la tastiera riattraversa la navbar a ogni pagina.
  [12-stile-sito.md](12-stile-sito.md#accessibilità) prende l'accessibilità come vincolo, non come
  rifinitura.
- **Cosa**: `<header>` intorno alla navbar, `<main id="main">` intorno a `@Body`, skip link come
  primo elemento focalizzabile, visibile solo al focus.
- **Fatto quando**: Tab dal caricamento della pagina offre "salta al contenuto" come prima tappa.

### B6 — Il menu mobile non si chiude navigando

- [ ] **Dove**: `Components/Layout/MainLayout.razor:14-17,62-66`
- **Perché**: `isMenuOpen` vive nel layout, che non viene re-inizializzato fra le pagine: aperto il
  menu e toccato un link, il menu resta aperto sopra la pagina di destinazione.
- **Cosa**: sottoscrivere `NavigationManager.LocationChanged` e chiudere il menu; chiudere anche
  con `Esc` e al click fuori.
- **Fatto quando**: toccare una voce di menu su telefono porta alla pagina con il menu chiuso.

### B7 — Il titolo sfora il viewport su mobile

- [ ] **Dove**: `wwwroot/css/base.css` (regole `h1`-`h3`)
- **Perché**: visibile in `fix-dashboard-mobile.png` — un'email lunga nell'`<h1>` esce dallo
  schermo a destra. Vale per qualunque stringa lunga senza spazi: nome di spazio, merchant, voce di
  lista.
- **Cosa**: `overflow-wrap: anywhere` sui titoli e sui contenitori che rendono contenuto utente.
- **Fatto quando**: un nome di spazio di 40 caratteri senza spazi non provoca scroll orizzontale
  a 360 px.

### B8 — Gli stati vuoti annunciano un'assenza invece di invitare

- [ ] **Dove**: `Home.razor` (386, 409, 432, 455, 478), `ShoppingList.razor:60`,
  `Expenses.razor:76`, `Reminders.razor:66`, `Notes.razor:64`, `Spaces.razor:26`
- **Perché**: [12-stile-sito.md](12-stile-sito.md#stati-vuoti-e-di-errore) porta *letteralmente*
  "Nessuna spesa trovata." come esempio da non seguire, contro "Non ci sono ancora spese in questo
  spazio. Registra la prima dal bot o da qui." Le chiavi attuali (`Expenses.Empty`,
  `Notes.Empty`, …) sono del primo tipo.
- **Cosa**: riscrivere i valori delle risorse esistenti — chiavi invariate, quindi nessun tocco al
  codice — aggiungendo in ciascuna l'azione successiva e un link dove ha senso. Le chiavi usate in
  due contesti (`ShoppingList.Empty` compare in pagina e in dashboard) vanno separate in due chiavi
  se il testo giusto differisce.
- **Fatto quando**: ogni stato vuoto della console nomina l'azione successiva.

### B9 — Nessuna indicazione della pagina corrente

- [ ] **Dove**: `Components/Layout/MainLayout.razor`
- **Perché**: la navbar usa `<a>` semplici: nulla indica dove ci si trova.
- **Cosa**: `NavLink` con `ActiveClass`, e `aria-current="page"` sulla voce attiva.
- **Fatto quando**: la voce corrispondente alla rotta corrente è distinguibile senza colore soltanto.

### B10 — Input fuori standard e senza etichetta

- [ ] **Dove**: `ShoppingList.razor:43-44`, e ogni altro `<input>` con `style` inline
- **Perché**: l'input di aggiunta ha padding e bordo scritti a mano invece della classe
  `.form-control` che esiste in `base.css:418` con `min-height: 44px` — quindi è sotto il target
  minimo toccabile richiesto da [12-stile-sito.md](12-stile-sito.md#accessibilità) — e ha solo un
  placeholder, che non è un'etichetta accessibile.
- **Cosa**: `.form-control` e `.form-floating` (già disponibili) su tutti gli input della console;
  `<label>` reale, anche se visivamente sostituito dal placeholder.
- **Fatto quando**: nessun `<input>` nella console porta uno `style` inline, e ognuno ha
  un'etichetta.

### B11 — Nessun riscontro dopo un'azione

- [ ] **Dove**: nuovo componente `Components/Shared/Toast.razor`, consumato dalle pagine che
  scrivono
- **Perché**: [12-stile-sito.md](12-stile-sito.md#movimento) menziona "la comparsa dei toast di
  conferma" fra i movimenti previsti, ma nessun toast esiste. Oggi un'azione riuscita si deduce dal
  fatto che la lista si è ricaricata; un'azione fallita compare come `alert-danger` in cima, fuori
  dal campo visivo su mobile.
- **Cosa**: un toast con `role="status"` per il successo e `role="alert"` per l'errore, rispettoso
  di `prefers-reduced-motion`, che sostituisca gli `alert` in cima alla pagina per il riscontro
  transitorio (gli errori persistenti restano dove sono).
- **Fatto quando**: aggiungere una voce mostra una conferma senza spostare il contenuto.

### B12 — Le pagine pubbliche non si possono cambiare di lingua

- [ ] **Dove**: `MainLayout.razor` (footer), `AuthenticatedUserRequestCultureProvider`
- **Perché**: il selettore di lingua è nel profilo, quindi dietro il login. Un visitatore italiano
  che arriva su una pagina servita in inglese — o viceversa — non ha modo di cambiarla, sulle
  stesse pagine che [06-roadmap.md](06-roadmap.md) elenca come prerequisito per la verification
  Google e che devono essere leggibili nelle lingue dichiarate.
- **Cosa**: selettore IT/EN nel footer, che per un utente anonimo scrive il cookie di cultura e per
  un utente autenticato aggiorna `User.PreferredCulture` (restando la fonte di verità, come
  richiesto da [09-localizzazione.md](09-localizzazione.md)).
- **Fatto quando**: un visitatore non autenticato cambia lingua e la scelta sopravvive alla
  navigazione.

### B13 — Stili inline al posto delle classi

- [ ] **Dove**: tutte le pagine; esistono solo due `.razor.css` (`MainLayout`, `ReconnectModal`)
- **Perché**: [12-stile-sito.md](12-stile-sito.md#implementazione-in-blazor) prescrive CSS
  isolation per componente. Oggi layout e spaziature vivono in centinaia di attributi `style`
  ripetuti (`style="display: flex; gap: var(--space-4); flex-wrap: wrap"` compare identico in
  decine di punti). Non è solo estetica: è il motivo per cui B3, B7 e B8 vanno corretti pagina per
  pagina invece che in un posto solo.
- **Cosa**: estrarre i tre o quattro pattern effettivamente ripetuti in utility di `base.css`
  (`.card-grid`, `.stack`, `.row-between`) e spostare il resto nel `.razor.css` del componente.
  Da fare **in coda ai lotti B**, riusando i pattern che gli interventi precedenti hanno già
  consolidato — non prima, o si rifattorizza due volte.
- **Fatto quando**: nessun `style` inline resta nelle pagine, esclusi i valori calcolati a runtime.

### B14 — Titoli di pagina mancanti

- [ ] **Dove**: `Components/Pages/NotFound.razor`, `LevelPicker.razor`
- **Cosa**: `<PageTitle>` localizzato (per `LevelPicker`, se è un componente e non una pagina, va
  verificato che non gli serva).
- **Fatto quando**: ogni rotta ha un titolo proprio nella scheda del browser.

### B15 — Nessun contesto di spazio persistente

- [ ] **Dove**: `MainLayout.razor`, pagine sotto `/spaces/{id}/...`
- **Perché**: dentro `/spaces/{id}/shopping-list` l'unico riferimento allo spazio è nell'`<h1>` e
  l'unica uscita è "torna alla dashboard". Con più spazi — il caso normale del prodotto secondo
  [02-modello-dati.md](02-modello-dati.md#condivisibile-per-costruzione) — passare dalla lista di
  Casa a quella di Personale richiede due navigazioni.
- **Cosa**: selettore di spazio nell'intestazione delle pagine con `SpaceId`, che cambia spazio
  restando sulla stessa risorsa, più un breadcrumb "Spazi › Casa › Lista della spesa".
- **Fatto quando**: si passa dalla lista di uno spazio a quella di un altro con un'interazione.

---

## Lotto C — Canali proattivi senza burocrazia

Sostituisce la Fase 3 (WhatsApp) come strada per raggiungere chi non usa Telegram. La motivazione
non è il costo dei template ma la Business Verification di Meta, impraticabile per un professionista
in regime forfettario senza iscrizione al registro imprese — vedi *Decisioni aperte*.

### C1 — `EmailChannel` e digest via email

- [ ] **Dove**: nuovo `Tessera.Channels/EmailChannel.cs`, `Jobs/DailyDigestJob.cs`,
  `IEmailSender`/`AzureEmailClient` (già esistenti, oggi usati solo da `ForgotPassword.razor:58`)
- **Perché**: è l'unico canale proattivo a zero burocrazia e costo trascurabile
  ([04-costi.md](04-costi.md) — ACS fattura per email inviata, cifre irrilevanti a questi volumi),
  e il dominio mittente è già verificato per il reset password. `DigestFormatter` produce già il
  testo. Sarebbe il terzo consumatore di `IChannel`, quello che giustifica
  `ChannelCapabilities` per come è stato progettato: `SupportsInlineKeyboard = false`,
  `SupportsProactiveFree = true`, `SupportsGroups = false`.
- **Cosa**: `EmailChannel : IChannel`; resa HTML del digest con gli stessi token di
  `tokens.css` in stile inline (i client di posta non leggono i CSS esterni); opt-in esplicito per
  utente (`User.EmailDigestEnabled`, default off) e link di disiscrizione a un click con token
  firmato — [07-compliance.md](07-compliance.md) considera una email proattiva senza consenso un
  trattamento senza base giuridica, non una notifica.
- **Fatto quando**: un utente senza Telegram collegato riceve il digest quotidiano per email e può
  disiscriversi senza fare login.
- **Dipende da**: nessuno. Il digest per più spazi (**E4**) è indipendente e può venire dopo.

### C2 — Web push sulla PWA

- [ ] **Dove**: `wwwroot/service-worker.js`, `wwwroot/manifest.webmanifest` (esistenti),
  `Tessera.Channels/WebChannel.cs`, `NotificationService`
- **Perché**: [06-roadmap.md](06-roadmap.md#canale-web-console-e-pwa) elenca "promemoria/digest
  proattivi anche sul canale web" fra le cose non fatte, e `WebChannel.Subscribe` oggi sostituisce
  la mailbox invece di affiancarla — quindi due schede aperte si escludono. Con il web push la
  console diventa un canale proattivo completo senza dipendere da nessun provider terzo.
- **Cosa**: sottoscrizione VAPID persistita per utente e per dispositivo, `WebChannel` che consegna
  al push quando la mailbox non è connessa, e `Subscribe` che diventa additivo.
- **Fatto quando**: un promemoria arriva come notifica di sistema sulla PWA installata a scheda
  chiusa.

### C3 — Aggregazione delle notifiche

- [ ] **Dove**: `Services/NotificationService.cs`
- **Perché**: dieci voci aggiunte alla lista producono dieci messaggi a ciascun altro membro.
  [04-costi.md](04-costi.md#mitigazioni-obbligatorie-prima-di-aprire-whatsapp) lo tratta come
  mitigazione obbligatoria per WhatsApp; senza WhatsApp resta buona igiene su Telegram e diventa
  **necessario** per l'email, dove dieci messaggi sono spam.
- **Cosa**: finestra di aggregazione di 60 s per (spazio × destinatario × tipo di evento), resa
  come un unico messaggio; su canali con `SupportsProactiveFree = false` o senza inline keyboard,
  finestra più lunga.
- **Fatto quando**: dieci aggiunte in un minuto producono una notifica per destinatario.
- **Dipende da**: conviene farlo prima o insieme a **C1**.

---

## Lotto D — Riassetto commerciale

Lo schema e il flusso di pagamento sono completi e testati in sandbox
([06-roadmap.md](06-roadmap.md#pagamenti)). Il problema è il listino sopra.

### D1 — Spostare i piani dagli assi di costo a quelli di valore

- [ ] **Dove**: `Tessera.Core/Spaces/SubscriptionPlan.cs`,
  `Configurations/SubscriptionPlanConfiguration.cs`, `UsageService`, `MessageProcessor`,
  `LinkService.CanLinkAnotherBotAsync`
- **Perché**: tre problemi strutturali.
  1. **Si vende un'unità di costo.** `Pricing.razor:46` mostra "chiamate al giorno" come
     caratteristica principale di ogni piano: è la metrica di fatturazione di Azure OpenAI.
     Peggio, L1/L2 non consumano quota, quindi lo stesso gesto consuma o no in base a *come* è
     stato scritto — un contatore che l'utente non può prevedere né capire.
  2. **Il piano gratuito paywalla la tesi del prodotto.** `MaxLinkedBots = 1` su Free: un membro
     con la chat privata collegata più un gruppo famiglia è già oltre. Ma la condivisione su
     Telegram ha costo marginale nullo ed è l'unico canale di crescita che il prodotto ha.
  3. **`AllowsReceiptScanning = false` su Free.** Lo scontrino che spunta la lista e registra la
     spesa è, per il [README](../README.md), la cosa che nessun concorrente fa. Nessun utente
     gratuito la prova mai, quindi nessuno capisce perché pagare.
- **Cosa**: due piani invece di quattro. Nuovi campi: `MaxReceiptsPerMonth`,
  `MaxLinkedCalendars`, `HistoryMonths`, `AllowsExport`. `MaxCallsPerDay` **resta nel codice** come
  protezione economica e sparisce dal listino. `MaxLinkedBots` va a un valore alto come
  anti-abuso. Assi a pagamento: scansioni scontrini oltre la soglia gratuita, calendari collegati
  oltre il primo, profondità dello storico interrogabile (prezzi e garanzie), disponibilità
  incrociate, export, spazi oltre il primo.
- **Fatto quando**: la pagina prezzi non nomina né chiamate né bot, un utente Free può scansionare
  qualche scontrino al mese, e la condivisione non è limitata su nessun piano.
- **Nota**: i prezzi effettivi sono una *Decisione aperta*, non parte di questo intervento.

### D2 — Ciclo annuale e periodo di prova

- [ ] **Dove**: `Tessera.Data/PayPalSubscriptionService.EnsurePlansProvisionedAsync`,
  `Tessera.Integrations/PayPalClient`
- **Perché**: PayPal Subscriptions supporta cicli annuali e `trial_period` senza codice nuovo di
  rilievo. L'annuale è il singolo intervento con più effetto su cassa e churn; il trial permette di
  far provare il piano completo senza svendere il gratuito.
- **Cosa**: un billing plan annuale per ogni piano a pagamento (le due colonne
  sandbox/live restano come sono,
  [02-modello-dati.md](02-modello-dati.md#abbonamento-paypal-per-spazio)), selettore
  mensile/annuale su `/pricing` e `/spaces/{id}/usage`.
- **Fatto quando**: il ciclo sottoscrivi → cambia piano → annulla è ripetuto in sandbox anche
  sull'annuale.
- **Dipende da**: **D1**.

### D3 — Telemetria di conversione

- [ ] **Dove**: `UsageService.TryRecordL3CallAsync`, `Pricing.razor`, `SpaceUsage.razor`
- **Perché**: si traccia `NotUnderstood` e la distribuzione del router, ma non un solo evento del
  percorso commerciale. Senza, non si sa dove si perdono le conversioni e ogni scelta di prezzo è
  cieca.
- **Cosa**: tre `TrackEvent` — limite di piano raggiunto (con quale limite), pagina prezzi vista,
  click su "sottoscrivi" — più l'esito del webhook.
- **Fatto quando**: l'imbuto limite → prezzi → click → attivazione è leggibile in Application
  Insights.

### D4 — Riscrivere la pagina prezzi

- [ ] **Dove**: `Components/Pages/Pricing.razor`
- **Perché**: oggi rende meccanicamente le righe di `SubscriptionPlan` — un elenco di numeri
  interni. Non dice cosa si ottiene.
- **Cosa**: descrizione per beneficio, confronto a due colonne, la domanda "cosa succede se
  smetto di pagare" con la risposta vera (nessuna perdita di dati, solo di funzioni oltre le
  soglie — [02-modello-dati.md](02-modello-dati.md#abbonamento-paypal-per-spazio)), e il
  chiarimento che il piano è **per spazio**, non per persona.
- **Fatto quando**: la pagina si legge senza conoscere il modello dati.
- **Dipende da**: **D1**.

### D5 — Verificare il checkout con carta senza conto PayPal

- [ ] **Dove**: configurazione dell'account PayPal, nessun codice
- **Perché**: la scelta di PayPal è fiscale e motivata
  ([04-costi.md](04-costi.md#provider-di-pagamento-paypal-non-stripe)), ma chi non ha un conto
  PayPal è la quota di abbandono più silenziosa del funnel.
- **Cosa**: verificare che il guest checkout con carta sia attivo per i piani di sottoscrizione, e
  se non lo è documentare il limite.
- **Fatto quando**: c'è una risposta scritta, in un senso o nell'altro.

---

## Lotto E — Nuove funzionalità

Ogni voce rispetta il criterio di ammissione di [06-roadmap.md](06-roadmap.md#fase-4--estensioni):
è una **connessione fra due funzioni esistenti**, non una voce autonoma in elenco. I non-obiettivi
dichiarati (divisione delle spese, meal planning, turni di casa, sync Alexa) restano fuori.

### E1 — Input vocale

- [ ] **Connette**: canale ↔ tutto. **Dove**: `MessageProcessor.HandleIncomingMediaAsync`,
  nuovo client accanto a `ReceiptVisionClient`
- **Perché**: [03-integrazioni.md](03-integrazioni.md#sostituto-per-il-caso-duso-voce) cerca un
  sostituto per il caso d'uso vocale dopo aver scartato Alexa. Il messaggio vocale di Telegram *è*
  quel sostituto: "mentre guido, aggiungi latte e pane" è il contesto in cui una lista della spesa
  si usa davvero. La pipeline degli allegati esiste già per gli scontrini, il costo di
  trascrizione è nell'ordine dei millesimi al minuto.
- **Cosa**: trascrizione dell'audio in ingresso, poi **lo stesso router** — un vocale che dice
  "aggiungi il latte" deve passare per L2 come il testo, non andare direttamente a L3. Soggetto
  alla stessa soglia giornaliera di `UsageService`, con un limite sulla durata.
- **Fatto quando**: un vocale con due voci da aggiungere le aggiunge entrambe e le rilegge in
  conferma.

### E2 — Promemoria di garanzia dallo scontrino

- [ ] **Connette**: scontrini ↔ promemoria. **Dove**: `MessageProcessor.HandleReceiptAsync`,
  `ReminderService`
- **Perché**: [06-roadmap.md](06-roadmap.md#fase-4--estensioni) segna l'archivio garanzie come già
  ottenuto "letteralmente gratis" dalla ricerca sullo storico. Manca il passo attivo: la garanzia
  serve *prima* che scada, non quando si cerca.
- **Cosa**: alla registrazione di uno scontrino con una riga sopra una soglia, proporre — con
  bottone, non creare d'autorità — un promemoria a 23 mesi.
- **Fatto quando**: uno scontrino con un elettrodomestico propone il promemoria una volta sola.

### E3 — Export delle spese

- [ ] **Connette**: spese ↔ console. **Dove**: `Components/Pages/Expenses.razor`, `ExpenseService`
- **Perché**: valore percepito alto, sforzo minimo, e copre insieme una richiesta pratica
  (portare i dati al commercialista o in un foglio di calcolo) e il diritto di portabilità che
  [07-compliance.md](07-compliance.md#adempimenti-minimi) richiede.
- **Cosa**: CSV per periodo e categoria, formattazione numerica e di data secondo la cultura
  dell'utente ([09-localizzazione.md](09-localizzazione.md)).
- **Fatto quando**: l'export si apre correttamente in Excel con cultura italiana.

### E4 — Digest su più spazi e digest periodico

- [ ] **Connette**: digest ↔ spese ↔ budget. **Dove**: `Jobs/DailyDigestJob.cs:54`, `DigestService`
- **Perché**: il job costruisce il digest **solo** per `User.DefaultSpaceId`: chi ha Casa più
  Personale più un gruppo vede un terzo della propria giornata. È un difetto, non un'estensione.
  Aggiungerci il riepilogo settimanale con confronto al periodo precedente è poi quasi gratis.
- **Cosa**: iterare gli spazi con permesso `Read`, con sezioni per spazio (l'omissione delle
  sezioni vuote esiste già); digest settimanale opzionale con andamento e stato del budget.
- **Fatto quando**: un utente con tre spazi vede tutti e tre nel digest.

### E5 — "Prenota" dopo la disponibilità incrociata

- [ ] **Connette**: freebusy ↔ creazione evento. **Dove**:
  `MessageProcessor.HandleCalendarFreeBusyQueryAsync`
- **Perché**: [06-roadmap.md](06-roadmap.md) chiama la disponibilità incrociata "la funzione più
  difendibile del prodotto". Oggi risponde "siete liberi giovedì dalle 18" e finisce lì: l'utente
  deve riformulare la creazione dell'evento a mano.
- **Cosa**: bottoni inline sulle fasce proposte che creano l'evento nel calendario di scrittura di
  default, riusando il flusso di conferma esistente.
- **Fatto quando**: da "quando siamo liberi giovedì?" si arriva all'evento creato con due tocchi.

### E6 — Prezzo per negozio

- [ ] **Connette**: `ExpenseLine` ↔ merchant. **Dove**: `LlmTools` (`query_price_history`),
  `ExpenseService`
- **Cosa**: estendere il confronto di prezzo esistente con la dimensione merchant ("il caffè costa
  meno da …"), senza modifiche di schema.
- **Fatto quando**: la domanda "dove conviene comprare X" ha una risposta se ci sono dati.

### E7 — Previsione di fine mese

- [ ] **Connette**: spese ricorrenti ↔ budget. **Dove**: `BudgetService`, `RecurringExpenseService`
- **Cosa**: proiezione a fine mese dalle ricorrenti non ancora generate più l'andamento corrente,
  esposta nel digest e nella pagina spese.
- **Fatto quando**: la proiezione compare quando c'è almeno una ricorrente attiva.

---

## Lotto F — Test e manutenibilità

### F1 — Ampliare il corpus del router

- [ ] **Dove**: `tests/Tessera.Ai.Tests/Routing/IntentRouterTests.cs`
- **Perché**: 12 casi `InlineData` per 22 matcher (11 IT, 11 EN). `CLAUDE.md` chiama questi test
  "i più preziosi del codebase" e avverte che regrediscono facilmente;
  [05-ottimizzazioni.md](05-ottimizzazioni.md#testare-il-router-è-essenziale) chiede un corpus
  crescente di frasi reali.
- **Cosa**: portare il corpus a coprire ogni matcher con almeno una formulazione positiva e una
  negativa (deve andare a L3), incluse le frasi che `05` elenca come non coperte — "manca il pane",
  "finito il detersivo", "serve il caffè". Se quelle passano a L3 quando potrebbero essere gestite
  a L2, sono anche il lavoro di **F1b**: aggiungere i matcher corrispondenti.
- **Fatto quando**: ogni matcher ha copertura in entrambe le direzioni.

### F2 — Test dei permessi

- [ ] **Dove**: `tests/Tessera.Core.Tests/Spaces/AccessPolicyTests.cs`,
  `Calendars/EffectiveCalendarLevelTests.cs`
- **Perché**: 68 righe per il modello a permessi granulari e 29 per il minimo a tre fattori.
  `CLAUDE.md`: "un errore lì fa fuoriuscire dati fra i membri di uno spazio".
- **Cosa**: tabella esaustiva livello richiesto × livello concesso × tipo di risorsa, più i casi
  limite del minimo `min(ProviderRole, ShareLevel, MembershipPermission)` (regola 15) e l'ex
  membro.
- **Fatto quando**: ogni combinazione di `AccessLevel` e `ResourceKind` è coperta.

### F3 — Scomporre `MessageProcessor`

- [ ] **Dove**: `Services/MessageProcessor.cs` — 2952 righe, 69 metodi
- **Perché**: non ha un solo test, e non è un caso: la classe fa ingestione, routing, permessi,
  resa e notifica insieme. È il file che ogni nuova funzione deve toccare.
- **Cosa**: estrarre gli handler per dominio (`ShoppingHandlers`, `ExpenseHandlers`,
  `CalendarHandlers`, `NoteHandlers`, `ReminderHandlers`) dietro un dispatch per intento, lasciando
  in `MessageProcessor` solo la pipeline. **Da fare a lotti, un dominio per commit**, e dopo i
  lotti A e B: una riscrittura in blocco di un file da 3000 righe senza test a copertura è
  esattamente il modo di introdurre regressioni invisibili.
- **Fatto quando**: `MessageProcessor` sta sotto le 500 righe e ogni handler ha test propri.

### F4 — Test dei servizi con database

- [ ] **Dove**: nuovo `tests/Tessera.Data.Tests`
- **Perché**: `Tessera.Data` è la parte più grande della soluzione e non ha test. Il database è
  condiviso fra sviluppo e produzione, quindi nessun test deve toccarlo.
- **Cosa**: SQLite in memoria per i servizi di dominio; i casi che valgono più degli altri sono
  `SpaceResolver` (la catena di precedenza a cinque passi), `UndoService`, `UsageService` e
  `AccountDeletionService` (pseudonimizzazione).
- **Fatto quando**: `dotnet test` copre la catena di risoluzione dello spazio senza toccare Azure
  SQL.

---

## Lotto G — Documentazione da aggiornare

I documenti sono la fonte di verità del progetto: le divergenze si scrivono lì, non solo nel codice.

- [ ] **G1** — [06-roadmap.md](06-roadmap.md): Fase 3 riscritta da "WhatsApp" a "canali proattivi
  senza burocrazia" (email, web push); WhatsApp declassato a Fase 5 accanto ad Alexa, con la
  motivazione vera — la Business Verification di Meta per un professionista in regime forfettario
  senza visura camerale, non il costo dei template
- [ ] **G2** — [04-costi.md](04-costi.md): via lo scenario Fase 3 a €150-400 e la soglia "50-100
  utenti su WhatsApp"; la soglia di sostenibilità si sposta a 200-300 utenti Telegram, il che rende
  la monetizzazione opportunistica anziché necessaria. Sezione "Piani a pagamento" allineata a
  **D1**
- [ ] **G3** — [09-localizzazione.md](09-localizzazione.md): la differenziazione digest-per-evento
  resta valida, ma il caso motivante diventa l'email (nessuna inline keyboard, un invio al giorno)
- [ ] **G4** — [01-architettura.md](01-architettura.md): `EmailChannel` come terzo `IChannel`;
  il recupero della coda al riavvio (**A4**) accanto al vincolo noto della coda in memoria
- [ ] **G5** — [03-integrazioni.md](03-integrazioni.md): sezione WhatsApp Cloud API marcata come
  non perseguita, con il motivo amministrativo esplicito
- [ ] **G6** — [12-stile-sito.md](12-stile-sito.md): se il lotto B introduce toast e selettore di
  tema, vanno descritti come componenti, non lasciati impliciti
- [ ] **G7** — [02-modello-dati.md](02-modello-dati.md): i nuovi campi di `SubscriptionPlan`
  (**D1**) e `User.EmailDigestEnabled` (**C1**)

---

## Decisioni aperte

Non sono voci di lavoro: sono scelte che condizionano più lotti e che vanno prese prima di
eseguirli.

1. **WhatsApp.** Considerato non perseguibile per la trafila di verification Meta, non per il
   costo. Se un giorno dovesse tornare, va tentato tramite un BSP che accompagni la verification, e
   come **esperimento di distribuzione** — nessuna decisione di prodotto, di prezzo o di roadmap
   deve dipendere dal suo esito. Il display name andrà probabilmente allineato alla ragione sociale.
2. **Il piano è per spazio o per chi paga?** Oggi è per spazio ed è deliberato
   ([02-modello-dati.md](02-modello-dati.md#piano-di-abbonamento)), ma chi paga per "Casa" ha il
   proprio spazio Personale su Free e non capirà perché lo scontrino funziona in una chat e non
   nell'altra. Da decidere: propagare l'entitlement agli spazi di chi paga, oppure rendere il
   confine esplicito in console. Blocca **D1** e **D4**.
3. **I prezzi effettivi.** €25/mese per un assistente familiare non ha comparabili nel mercato di
   riferimento, che sta nell'ordine di pochi euro al mese — da verificare sui listini attuali dei
   concorrenti citati nel [README](../README.md) prima di fissare le cifre. Blocca **D1**.
4. **Forfettario e soglia OSS**, già aperta in [04-costi.md](04-costi.md#piani-a-pagamento): da
   confermare con il commercialista. Non blocca nulla di tecnico, ma condiziona cosa mostrare in
   fattura.

---

## Ordine consigliato

| Passo | Contenuto | Perché in questa posizione |
|---|---|---|
| 1 | **A1, A2, A6** ✅ | Token, latenza, misurabilità. Tutto già progettato in `docs/`, nessuna decisione da prendere |
| 2 | **A4, A5** | Correttezza e operabilità: il messaggio perso è un difetto silenzioso, e si sistema in poche ore |
| 3 | **A3** + **F1** | Lo storico conversazionale è la retention; il corpus del router lo protegge dalle regressioni |
| 4 | **B1, B2, B3, B4, B6, B7** | Sei interventi visibili e circoscritti. B3 è il più importante: è la pagina a uso quotidiano |
| 5 | **B5, B8, B9, B10, B11, B14** | Accessibilità, riscontro, tono. B8 è solo riscrittura di risorse |
| 6 | **F2** | I permessi, prima di aggiungere superficie che li usa |
| 7 | **C3, C1** | Aggregazione e poi email: il canale che sostituisce WhatsApp |
| 8 | **D3**, poi decisioni aperte 2 e 3, poi **D1, D4, D2, D5** | La telemetria prima del riassetto: si decide su dati, non a memoria |
| 9 | **E3, E2, E4, E1** | Export e garanzie sono quasi gratis; la voce merita di stare dopo perché tocca la pipeline |
| 10 | **B12, B13, B15**, **F3**, **F4** | Manutenibilità e rifiniture, quando i pattern si sono consolidati |
| 11 | **C2**, **E5, E6, E7** | Il resto, senza urgenza |

**G1-G5 vanno fatti subito**, fuori da questa scaletta: sono divergenze già accertate fra
documentazione e realtà, e finché restano aperte ogni rilettura di `docs/` riparte da premesse
sbagliate. **G6** e **G7** seguono i lotti che li generano.
