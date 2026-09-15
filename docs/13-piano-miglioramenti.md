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

### A3 — Storico conversazionale in L3 ✅

- [x] **Dove**: `Tessera.Ai/Llm/LlmFallbackClient.TryCompleteAsync`, `LlmContext`,
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
- **Fatto**: nuovo record `RecentExchange` (testo utente, nome del tool chiamato, argomenti del
  tool, timestamp) in `Tessera.Core.Conversations`, con `Parse`/`Serialize` che filtrano per TTL
  a 30 min e tengono gli ultimi 4 — testato in isolamento senza database
  (`RecentExchangeTests.cs`, 7 casi). Reso nella coda variabile del prompt come elenco "Recent
  messages in this conversation", con un paragrafo aggiunto al system prompt (statico, non rompe
  la cache) che istruisce il modello a usarlo solo per risolvere un follow-up, mai per
  giustificare un'azione che il messaggio corrente non chiede. **Deviazione dal testo del
  lotto**: niente riuso di `ConversationState.StateJson` — quella colonna, insieme a
  `PendingIntent`, è già uno slot condiviso da sei flussi di conferma diversi (conferma
  promemoria, conferma/spostamento/cancellazione evento calendario, scelta spazio, fallback
  permessi): scriverci sopra anche lo storico degli scambi avrebbe fatto sì che una conferma in
  sospeso e lo storico si sovrascrivessero a vicenda. Aggiunta invece `RecentExchangesJson`, una
  colonna nuova sulla stessa riga, con TTL calcolato per-entry (non sul campo `ExpiresAt`
  condiviso, che resta di competenza esclusiva del meccanismo di conferma). Migrazione
  `AddRecentExchangesToConversationState` generata e applicata.
- **Nota**: il criterio di completamento è verificato a livello di corpus del router (la frase
  di follow-up "e a febbraio?" è aggiunta come caso L3 esplicito in `IntentRouterTests.cs`, con
  commento che rimanda a questa voce) — una verifica end-to-end contro un vero completamento
  Azure OpenAI resta fuori portata dei test unitari esistenti, che oggi non toccano
  `LlmFallbackClient`.

### A4 — Messaggio perso al riavvio ✅

- [x] **Dove**: `Tessera.Web/Endpoints/TelegramUpdateIngestor.cs:37-51`,
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
- **Fatto**: `ProcessedMessage` porta `CompletedAt` (nullable) e `PayloadJson` — l'ingestor
  serializza l'`InboundMessage` intero alla scrittura della riga di dedup, `MessageProcessor`
  valorizza `CompletedAt` in un `finally` attorno a `ProcessAsync` (sia in caso di successo sia
  di eccezione: un messaggio che è già fallito una volta con una risposta di scuse all'utente
  non deve essere rigiocato a ogni sweep successivo). Nuovo `IScheduledJob`,
  `PendingMessageRecoveryJob`, rimette in coda le righe con `CompletedAt IS NULL` più vecchie di
  un minuto (registrato solo quando Telegram è configurato). **Scoperta non anticipata dal
  testo del lotto**: la deduplica in `TelegramUpdateIngestor.IngestAsync` **non** è stata
  cambiata a "scarta solo le righe completate" come scritto sopra — resta "qualunque riga
  esistente blocca un nuovo enqueue", perché altrimenti un vero retry di Telegram durante
  un'elaborazione ancora legittimamente in corso (non morta, solo lenta) avrebbe causato una
  doppia elaborazione. La soglia di un minuto nel job di recupero è ciò che distingue
  "orfano da riavvio" da "ancora in corso", non la deduplica in ingresso.

### A5 — Health check e pulizia ✅

- [x] **Dove**: `Program.cs`, nuovo `IScheduledJob`
- **Perché**: non esiste un endpoint di salute (App Service non ha modo di distinguere "vivo" da
  "risponde ma la coda è bloccata"), e `ProcessedMessages` cresce senza limite.
- **Cosa**: `MapHealthChecks("/health")` con un check su DB e uno sull'ultima elaborazione
  riuscita; `ProcessedMessagePurgeJob` che elimina le righe completate oltre i 7 giorni.
- **Fatto quando**: `/health` risponde e il job compare nei log dello scheduler.
- **Fatto**: `HealthChecks/{DatabaseHealthCheck,MessageProcessingHealthCheck}.cs` — il secondo
  segnala `Degraded` solo se una riga è ferma da più di 5 minuti (non "nessun traffico di
  recente", che per un bot personale è normale, non un problema). `ProcessedMessagePurgeJob`
  elimina sia le righe Telegram completate da più di 7 giorni sia le righe PayPal (che non hanno
  mai `CompletedAt`, essendo sincrone) più vecchie di 7 giorni da `ProcessedAt`. Migrazione EF
  generata (`AddProcessedMessageCompletion`) ma **non applicata** al database condiviso — resta
  da eseguire come passo CI/CD, come da `CLAUDE.md`.

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

### B1 — Il saluto mostra l'indirizzo email ✅

- [x] **Dove**: `Components/Pages/Home.razor:33`
- **Perché**: `context.User.Identity?.Name` per ASP.NET Core Identity è lo username, cioè l'email.
  La dashboard accoglie l'utente con "Welcome back, mario.rossi@example.com". `User.DisplayName`
  esiste, è già popolato dalle claim Google, ed è già letto da `MainLayout.razor` per la navbar —
  [06-roadmap.md](06-roadmap.md#login-social-e-profilo-utente) lo dà per fatto in console.
- **Cosa**: usare `DisplayName` con fallback alla parte locale dell'email, non all'indirizzo intero.
- **Fatto quando**: un account senza `DisplayName` vede "Welcome back, mario", non l'email.
- **Fatto**: `GreetingName(User)` in `Home.razor` — `DisplayName` se presente, altrimenti la parte
  di `Email` prima di `@`. Riusa `user` già caricato da `UserProvisioningService.GetAsync` in
  `OnInitializedAsync`, nessuna query aggiuntiva.

### B2 — La dark mode è definita ma irraggiungibile ✅

- [x] **Dove**: `wwwroot/css/tokens.css:51`, `MainLayout.razor`
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
- **Fatto**: valori esadecimali della palette scura estratti in variabili `--dark-*` a `:root`,
  consumate sia da `[data-theme="dark"]` (il toggle manuale) sia da un nuovo blocco
  `@media (prefers-color-scheme: dark) { :root:not([data-theme]) { ... } }` — nessun hex
  duplicato fra i due. Script inline sincrono in `App.razor`, prima dei fogli di stile, legge
  `localStorage` e imposta `data-theme` sull'`<html>` prima che il CSS venga anche solo
  richiesto (niente flash del tema sbagliato). Toggle a tre stati (Auto/Chiaro/Scuro) in
  `/settings`, `wwwroot/js/theme.js` (`window.tesseraTheme.set/get`), letto via `IJSRuntime` in
  `OnAfterRenderAsync` (l'interop non è disponibile prima).

### B3 — La riga della lista della spesa ✅

È la pagina a uso più frequente della console e la peggiore delle interazioni. Quattro problemi
distinti, uno stesso intervento.

- [x] **Dove**: `Components/Pages/ShoppingList.razor` (righe 60-88), `wwwroot/css/base.css:518-553`
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
- **Fatto**: nuovo `ShoppingList.razor.css` (CSS isolation, non condiviso con `.list-row` di
  Reminders/Expenses — un layout genuinamente diverso, non una variante). Checkbox nativa come
  azione primaria (`accent-color: var(--teal)`), etichetta intera cliccabile ≥44px; rimuovi come
  icona `✕` trasparente, colore `--danger` solo all'hover, mai un bottone a piena larghezza;
  `busyItemIds: HashSet<Guid>` per lo stato "in corso" per riga, con aggiornamento ottimistico
  (`item.IsChecked = true`/rimozione dalla lista locale prima della risposta del server) e
  rollback su `UnauthorizedAccessException`; sezione "Già preso (n)" separata in fondo;
  conteggio "{n} da comprare" in cima. `ClearAsync` invariato (resta `--danger`, conferma
  esistente), ma ora sotto un ulteriore `seam` di separazione.
- **Corretto dopo la prima stesura, su segnalazione diretta dell'utente**: la prima versione
  renderizzava la checkbox delle voci già prese come `checked disabled` (non interattiva),
  giustificato all'epoca da `CheckItemByIdAsync` non avendo un equivalente "uncheck"
  (`docs/02-modello-dati.md`) — ma questo lascia chi spunta una voce per sbaglio, o vuole
  rimetterla in lista, senza via d'uscita nella console, che non ha un bottone di undo come il
  bot. Aggiunto `ShoppingListService.UncheckItemByIdAsync` (simmetrico a `CheckItemByIdAsync`,
  stesso pattern), e la checkbox è ora un vero toggle in entrambe le direzioni, ottimistico con
  rollback su `UnauthorizedAccessException` in entrambi i sensi. Non è lo stesso meccanismo
  dell'`/undo` del bot (finestra di 10 minuti, un'operazione sola, per utente,
  [10-conversazione.md](10-conversazione.md)): è un togle sempre disponibile, più adatto alla
  console che non ha una superficie di undo propria. Verificato visivamente (spunta → "Già
  preso" → spunta di nuovo → torna in "da comprare", checkbox interattiva in entrambi gli
  stati).
- **Bug trovato e corretto tramite verifica visiva reale** (screenshot con Playwright contro
  l'app avviata, non solo build/test): le due `@foreach` su `UncheckedItems`/`CheckedItems`
  senza `@key` facevano sì che, quando una voce passava da una lista all'altra, il diffing di
  Blazor per posizione lasciasse lo stato nativo `checked` della checkbox sulla riga sbagliata
  (es. si spunta "Latte", e nello screenshot successivo è "Pane" — la riga successiva nella
  stessa posizione — a comparire visivamente spuntata, pur restando in "da comprare" e senza
  barratura). Aggiunto `@key="item.Id"` sul `<li>` in `RenderRow`. Nessun build o test lo
  avrebbe intercettato: è esattamente la classe di bug per cui questa sessione ha chiesto una
  verifica visiva prima di considerare il lotto concluso.

### B4 — Variabile CSS inesistente ✅

- [x] **Dove**: `Components/Pages/ShoppingList.razor:68`
- **Perché**: usa `var(--text-soft)`, che non esiste in `tokens.css` — `.text-soft` è una *classe*
  (`base.css:377`), il token si chiama `--ink-soft`. Le voci spuntate ottengono la barratura ma non
  l'attenuazione. È l'unico riferimento a un token inesistente in tutto il progetto (verificato
  incrociando definizioni e usi).
- **Cosa**: `var(--ink-soft)`, e la regola va nel CSS di componente, non in uno `style` inline.
- **Fatto quando**: una voce spuntata è visibilmente attenuata oltre che barrata.
- **Fatto**: assorbito nella riscrittura di B3 — `.shopping-row-text-checked` in
  `ShoppingList.razor.css` usa `var(--ink-soft)`.

### B5 — Landmark e navigazione da tastiera ✅ (con una riserva)

- [x] **Dove**: `Components/Layout/MainLayout.razor`
- **Perché**: `@Body` non è dentro un `<main>`, non c'è `<header>`, non c'è skip link. Chi usa uno
  screen reader o la tastiera riattraversa la navbar a ogni pagina.
  [12-stile-sito.md](12-stile-sito.md#accessibilità) prende l'accessibilità come vincolo, non come
  rifinitura.
- **Cosa**: `<header>` intorno alla navbar, `<main id="main">` intorno a `@Body`, skip link come
  primo elemento focalizzabile, visibile solo al focus.
- **Fatto quando**: Tab dal caricamento della pagina offre "salta al contenuto" come prima tappa.
- **Fatto**: `<header class="navbar">`, `<main id="main">`, skip link `.skip-link` (off-canvas,
  visibile al focus) come primo figlio del layout.
- **Riserva emersa dalla verifica dal vivo**: il criterio "Fatto quando" **non è pienamente
  soddisfatto**. `Routes.razor` ha già `<FocusOnNavigate RouteData="routeData" Selector="h1" />`
  (Blazor, pre-esistente), che sposta il focus sull'`<h1>` a ogni navigazione — inclusa la prima.
  Verificato con Playwright: `document.activeElement` è già l'`<h1>` subito dopo il caricamento,
  *prima* di premere Tab. Lo skip link, posizionato prima nel DOM, non è mai la prima tappa reale
  — un Tab dal caricamento passa all'elemento *dopo* l'h1, non torna indietro. Nella pratica il
  problema che B5 voleva risolvere è già in gran parte coperto da `FocusOnNavigate` (il focus
  salta comunque oltre la navbar, senza bisogno di premere Tab), e lo skip link resta un
  meccanismo di riserva per i casi in cui `FocusOnNavigate` non si applica (screen reader che
  gestiscono il focus diversamente, ingressi che non passano dal router di Blazor) — non dannoso,
  ma non è "la prima tappa" come scritto. Non ho modificato `FocusOnNavigate` (funzionalità
  distinta, pre-esistente, probabilmente deliberata): correggere questa interazione richiede una
  decisione di prodotto (rimuovere l'auto-focus sull'h1, o accettare che lo skip link sia un
  backup silenzioso) che non mi competeva prendere qui.

### B6 — Il menu mobile non si chiude navigando ✅

- [x] **Dove**: `Components/Layout/MainLayout.razor:14-17,62-66`
- **Perché**: `isMenuOpen` vive nel layout, che non viene re-inizializzato fra le pagine: aperto il
  menu e toccato un link, il menu resta aperto sopra la pagina di destinazione.
- **Cosa**: sottoscrivere `NavigationManager.LocationChanged` e chiudere il menu; chiudere anche
  con `Esc` e al click fuori.
- **Fatto quando**: toccare una voce di menu su telefono porta alla pagina con il menu chiuso.
- **Fatto**: `NavigationManager.LocationChanged` chiude il menu su ogni navigazione (con
  `IDisposable` per la sottoscrizione). Escape gestito in puro Blazor (`@onkeydown` sul
  contenitore `.navbar`, nessun JS). Click fuori via nuovo `wwwroot/js/nav.js`
  (`window.tesseraNav.init`, un solo listener `document` registrato una volta in
  `OnAfterRenderAsync`, che legge lo stato dal DOM — classe `nav-open` — invece di essere
  aperto/chiuso a ogni toggle, evitando qualunque problema di sincronizzazione con i re-render
  di Blazor) con callback `[JSInvokable] CloseMenuFromOutsideClick`.

### B7 — Il titolo sfora il viewport su mobile ✅

- [x] **Dove**: `wwwroot/css/base.css` (regole `h1`-`h3`)
- **Perché**: visibile in `fix-dashboard-mobile.png` — un'email lunga nell'`<h1>` esce dallo
  schermo a destra. Vale per qualunque stringa lunga senza spazi: nome di spazio, merchant, voce di
  lista.
- **Cosa**: `overflow-wrap: anywhere` sui titoli e sui contenitori che rendono contenuto utente.
- **Fatto quando**: un nome di spazio di 40 caratteri senza spazi non provoca scroll orizzontale
  a 360 px.
- **Fatto**: `overflow-wrap: anywhere` su `html, body` invece che titolo per titolo — si applica a
  ogni card/paragrafo/span che renderizza contenuto utente senza dover toccare ogni componente
  singolarmente, e nessuna nuova pagina può reintrodurre il bug per omissione.
- **Fatto quando**: un nome di spazio di 40 caratteri senza spazi non provoca scroll orizzontale
  a 360 px.

### B8 — Gli stati vuoti annunciano un'assenza invece di invitare ✅

- [x] **Dove**: `Home.razor` (386, 409, 432, 455, 478), `ShoppingList.razor:60`,
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
- **Fatto**: `Spaces.Empty` e i due testi solo-dashboard (`Dashboard.NoPreviews`,
  `Dashboard.TodayEmpty`) erano già nel formato giusto — nessuna modifica. Per gli altri quattro
  (`ShoppingList`/`Expenses`/`Reminders`/`Notes`), le chiavi condivise fra pagina e anteprima
  dashboard **sono state separate**: la dashboard mantiene la chiave originale, breve, corretta
  lì perché il link "View X →" è già adiacente; la pagina intera usa una nuova chiave
  `*.PageEmpty` che nomina l'azione (tutte e quattro le pagine hanno un modulo di aggiunta
  proprio sopra, quindi "Aggiungi qui sopra" è sempre vero). Nessuna modifica a `Home.razor`.

### B9 — Nessuna indicazione della pagina corrente ✅

- [x] **Dove**: `Components/Layout/MainLayout.razor`
- **Perché**: la navbar usa `<a>` semplici: nulla indica dove ci si trova.
- **Cosa**: `NavLink` con `ActiveClass`, e `aria-current="page"` sulla voce attiva.
- **Fatto quando**: la voce corrispondente alla rotta corrente è distinguibile senza colore soltanto.
- **Fatto**: ogni `<a>` della navbar è ora un `NavLink` con `ActiveClass="nav-active"` (`aria-current`
  è automatico, comportamento nativo di `NavLink`); `.nav-active` aggiunge peso e sottolineatura,
  non solo colore. Verificato dal vivo: su `/settings` solo "Settings" risulta attivo; su una
  pagina sotto `/spaces/{id}/...` risulta attivo "Your spaces" (match di prefisso, deliberato —
  `/spaces` è comunque "dove ci si trova" anche annidati).

### B10 — Input fuori standard e senza etichetta ✅ (con un'eccezione dichiarata)

- [x] **Dove**: `ShoppingList.razor:43-44`, e ogni altro `<input>` con `style` inline
- **Perché**: l'input di aggiunta ha padding e bordo scritti a mano invece della classe
  `.form-control` che esiste in `base.css:418` con `min-height: 44px` — quindi è sotto il target
  minimo toccabile richiesto da [12-stile-sito.md](12-stile-sito.md#accessibilità) — e ha solo un
  placeholder, che non è un'etichetta accessibile.
- **Cosa**: `.form-control` e `.form-floating` (già disponibili) su tutti gli input della console;
  `<label>` reale, anche se visivamente sostituito dal placeholder.
- **Fatto quando**: nessun `<input>` nella console porta uno `style` inline, e ognuno ha
  un'etichetta.
- **Fatto**: `.form-floating`/`.form-control` su `ShoppingList` (voce), `Reminders` (testo, con
  `<label class="form-label">` separate per Data/Ora — i `<input type=date/time>` nativi non si
  prestano bene al pattern a etichetta fluttuante), `InviteMember` (link di invito, in sola
  lettura, con `aria-label`). Nuove chiavi di risorsa per le etichette (`ShoppingList.AddLabel`,
  `Reminders.TextLabel`/`DateLabel`/`TimeLabel`, `InviteMember.LinkFieldLabel`).
  **Eccezione dichiarata**: l'input di `InviteMember` mantiene `style="flex: 1; font-family: var(--font-mono);"`
  inline (il layout flessibile e il font monospazio sono contestuali, non standardizzabili senza
  anticipare l'estrazione di utility che B13 rimanda apposta a dopo). `<select>` (Calendars,
  Expenses, LevelPicker) restano fuori: il testo del lotto parla esplicitamente di `<input>`, i
  `<select>` sono materia di B13.

### B11 — Nessun riscontro dopo un'azione ✅

- [x] **Dove**: nuovo componente `Components/Shared/Toast.razor`, consumato dalle pagine che
  scrivono
- **Perché**: [12-stile-sito.md](12-stile-sito.md#movimento) menziona "la comparsa dei toast di
  conferma" fra i movimenti previsti, ma nessun toast esiste. Oggi un'azione riuscita si deduce dal
  fatto che la lista si è ricaricata; un'azione fallita compare come `alert-danger` in cima, fuori
  dal campo visivo su mobile.
- **Cosa**: un toast con `role="status"` per il successo e `role="alert"` per l'errore, rispettoso
  di `prefers-reduced-motion`, che sostituisca gli `alert` in cima alla pagina per il riscontro
  transitorio (gli errori persistenti restano dove sono).
- **Fatto quando**: aggiungere una voce mostra una conferma senza spostare il contenuto.
- **Fatto**: `ToastService` (Scoped, un evento) + `Toast.razor` renderizzato una sola volta in
  `MainLayout`, posizione fissa in basso, `role="status"`/`role="alert"`, si auto-dismissa dopo 4s
  (cancellabile se arriva un nuovo toast prima), rispetta `prefers-reduced-motion`. Sostituisce
  gli `alert-danger` transitori (non quelli persistenti — qui non ce n'erano) in
  `ShoppingList`/`Reminders`/`Expenses`/`Notes.razor`: conferma di successo su ogni scrittura
  (aggiungi/spunta/rimuovi/svuota per la lista; aggiungi/completa per i promemoria; registra per
  le spese; salva/modifica/elimina per le note), errore verso toast anziché alert fisso. Verificato
  dal vivo: "Added: Yogurt" poi "Checked off: Yogurt" compaiono e scompaiono correttamente senza
  spostare il contenuto della pagina.

### B12 — Le pagine pubbliche non si possono cambiare di lingua ✅

- [x] **Dove**: `MainLayout.razor` (footer), `AuthenticatedUserRequestCultureProvider`
- **Perché**: il selettore di lingua è nel profilo, quindi dietro il login. Un visitatore italiano
  che arriva su una pagina servita in inglese — o viceversa — non ha modo di cambiarla, sulle
  stesse pagine che [06-roadmap.md](06-roadmap.md) elenca come prerequisito per la verification
  Google e che devono essere leggibili nelle lingue dichiarate.
- **Cosa**: selettore IT/EN nel footer, che per un utente anonimo scrive il cookie di cultura e per
  un utente autenticato aggiorna `User.PreferredCulture` (restando la fonte di verità, come
  richiesto da [09-localizzazione.md](09-localizzazione.md)).
- **Fatto quando**: un visitatore non autenticato cambia lingua e la scelta sopravvive alla
  navigazione.
- **Fatto**: nuovo endpoint `GET /set-culture?culture=it|en&returnUrl=...` (`CultureEndpoints.cs`)
  — scrive il cookie standard `CookieRequestCultureProvider` sempre, e in più aggiorna
  `User.PreferredCulture` se la richiesta è autenticata (stesso metodo
  `UserProvisioningService.SetPreferredCultureAsync` già usato da `/language` sul bot e da
  `Profile.razor`). `Results.LocalRedirect`, non `Redirect`: `returnUrl` arriva dalla query
  string di un endpoint anonimo, un redirect aperto sarebbe un vettore di phishing.
  `CookieRequestCultureProvider` aggiunto a `RequestCultureProviders`, in mezzo — dopo la
  preferenza DB (che resta la fonte di verità da autenticati) e prima di `Accept-Language`.
  Selettore "English"/"Italiano" nel footer (nomi non tradotti, stessa convenzione già in uso
  nel `<select>` di `Profile.razor`), stato attivo segnato con peso/sottolineatura come
  `.nav-active`. Verificato dal vivo: click su "Italiano" → contenuto in italiano, cookie
  `c=it|uic=it` impostato, navigazione a `/pricing` mantiene l'italiano.
- **Trovato verificando dal vivo, corretto nello stesso intervento**: `<html lang="en">` in
  `App.razor` era fisso, indipendente dalla cultura reale della richiesta — uno screen reader
  annunciava una pagina in italiano come se fosse inglese. Ora legge
  `CultureInfo.CurrentUICulture` (già negoziata dal middleware quando `App.razor` renderizza).

### B13 — Stili inline al posto delle classi ✅

- [x] **Dove**: tutte le pagine; esistono solo due `.razor.css` (`MainLayout`, `ReconnectModal`)
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
- **Fatto**: aggiunte a `base.css` le utility effettivamente ripetute *fra più file* (non solo
  dentro una pagina): `.stack`/`.stack-sm` (colonna flex, gap `--space-3`/`--space-2`),
  `.card-grid` (la riga flex con wrap usata sia per griglie di card sia per righe di azioni),
  `.grid-item` (`flex: 1; min-width: 14rem`) e `.card-link` (rimuove colore/sottolineatura da
  un'ancora che è visivamente una `.card`), `.inline-row` e `.checkbox-label` (riga flex compatta
  per checkbox/radio + etichetta), `.select-compact` (il `<select>` più stretto di
  `.form-control`, usato da `Calendars.razor` e `SpaceCalendars.razor`), più una famiglia minima
  di utility di spaziatura/larghezza (`.m-0`, `.mb-0`, `.mt-1`…`.mt-8`, `.mb-1`/`.mb-2`/
  `.mb-space-3`/`.mb-6`/`.mb-8`, `.ml-2`/`.ml-3`, `.max-w-32`/`.max-w-40`/`.max-w-42`, `.text-sm`)
  che mappano direttamente sul token dello stesso numero — `.mb-3` preesistente non è stato
  toccato: resta l'eccezione voluta per la parità con la spacer scale di Bootstrap usata dalle
  pagine Identity scaffolded. Gli `<input>`/`<textarea>` che duplicavano `.form-control` ad-hoc
  (`padding: var(--space-2); border: 1px solid var(--line); border-radius: var(--radius-sm)`)
  ora usano la classe condivisa; i `<select>` con lo stesso pattern sono rimasti fuori
  deliberatamente (padding diverso, mai stati nel perimetro di B11) e usano `.select-compact`,
  condivisa fra `Calendars.razor`, `SpaceCalendars.razor` ed `Expenses.razor` — la prima stesura
  aveva duplicato lo stesso pattern in `Expenses.razor.css` come `.expense-category-select`,
  accorpata qui perché era un doppione esatto. Ogni stile rimasto specifico di una pagina è
  finito nel `.razor.css` del componente, con nomi semantici (non `.style-1`): nuovi file per
  `LevelPicker`, `AccountDelete`, `FaqSection`, `Chat`, `Calendars`, `Notes`, `Settings`,
  `SpaceDetail`, `SpaceCalendars`, `Spaces`, `Profile`, `Pricing`, `Home`; `ShoppingList.razor.css`
  (già esistente) ha guadagnato una regola in più. Le due eccezioni dichiarate sono rimaste intatte: il corpo HTML dell'email in
  `ForgotPassword.razor` (`BuildResetPasswordEmailHtml`, CSS inline richiesto dai client di
  posta) e lo `style="flex: 1; font-family: var(--font-mono);"` sull'input del link d'invito in
  `InviteMember.razor` (eccezione già decisa in B12). Gli unici `style` rimasti nell'intero
  albero `Components/` sono queste due eccezioni più i valori con interpolazione `@` a runtime
  (`SpaceUsage.razor`, `UserAvatar.razor`, e il `margin-bottom` condizionale di `Home.razor`).
  Nessuna logica C#/code-behind toccata: solo markup e CSS, `dotnet build` a zero warning e
  `dotnet test` verde.

### B14 — Titoli di pagina mancanti ✅

- [x] **Dove**: `Components/Pages/NotFound.razor`, `LevelPicker.razor`
- **Cosa**: `<PageTitle>` localizzato (per `LevelPicker`, se è un componente e non una pagina, va
  verificato che non gli serva).
- **Fatto quando**: ogni rotta ha un titolo proprio nella scheda del browser.
- **Fatto**: `LevelPicker.razor` confermato essere un componente semplice (nessun `@page`, usato
  dentro `SpaceDetail`), non gli serve. `NotFound.razor` ora ha `<PageTitle>` localizzato
  (`NotFound.Title`/`NotFound.Body`, nuove chiavi) — e in passato aveva anche testo inglese
  scritto direttamente nel markup (`"Not Found"`, violazione della regola sulla lingua), corretto
  nello stesso intervento.
- **Scoperta non prevista da questa voce, trovata verificando dal vivo**: il markup che l'utente
  *vede davvero* per un URL non esistente non passava da `NotFound.razor` affatto. `Routes.razor`
  aveva il proprio `<NotFound>` del `Router` con markup inglese cablato a mano, duplicato e
  scollegato da `NotFound.razor` (che era raggiungibile solo navigando esplicitamente a
  `/not-found`). Ho unificato i due — `Routes.razor` ora renderizza `<NotFound />` (il componente)
  dentro il proprio `LayoutView`, e `NotFound.razor` non ha più un `@layout` proprio (era comunque
  ignorato in questo punto d'uso). **Ma ho poi trovato, con una verifica dal vivo via curl, che
  questo ramo di `Router` in pratica non viene mai raggiunto in questa app**: `Program.cs`
  configura `UseStatusCodePagesWithReExecute("/not-found", ...)`, che intercetta il 404 a livello
  HTTP e dovrebbe rieseguire la pipeline sulla pagina `/not-found` prima ancora che il `Router` di
  Blazor entri in gioco. **Verificato che questo non funziona**: un URL inesistente restituisce
  oggi `404` con corpo *completamente vuoto* (`Content-Length: 0`), non la pagina "not found".
  Confermato via `git log` che `UseStatusCodePagesWithReExecute` esisteva già prima di questa
  sessione (commit `6cf3d81`) — **non è una regressione introdotta qui**, ma un bug pre-esistente
  scoperto per la prima volta durante questa verifica. Non l'ho corretto: tocca la pipeline HTTP
  centrale (ordine dei middleware, `UseWhen`, assenza di un `UseRouting()` esplicito), è
  potenzialmente delicato, ed è fuori dallo scope letterale di B14. Segnalato all'utente
  separatamente per decidere se e quando intervenire.

### B15 — Nessun contesto di spazio persistente ✅

- [x] **Dove**: `MainLayout.razor`, pagine sotto `/spaces/{id}/...`
- **Perché**: dentro `/spaces/{id}/shopping-list` l'unico riferimento allo spazio è nell'`<h1>` e
  l'unica uscita è "torna alla dashboard". Con più spazi — il caso normale del prodotto secondo
  [02-modello-dati.md](02-modello-dati.md#condivisibile-per-costruzione) — passare dalla lista di
  Casa a quella di Personale richiede due navigazioni.
- **Cosa**: selettore di spazio nell'intestazione delle pagine con `SpaceId`, che cambia spazio
  restando sulla stessa risorsa, più un breadcrumb "Spazi › Casa › Lista della spesa".
- **Fatto quando**: si passa dalla lista di uno spazio a quella di un altro con un'interazione.
- **Fatto**: nuovo componente condiviso `Components/Shared/SpaceResourceHeader.razor` — breadcrumb
  "Your spaces › {Spazio} › {Risorsa}" più un `<select>` che elenca ogni altro spazio dove
  l'utente ha almeno il livello minimo richiesto per quella risorsa (calcolato con
  `AccessPolicy.CanAsync` per ciascuno spazio di appartenenza, non solo quello corrente).
  Collegato a `ShoppingList`, `Reminders`, `Expenses`, `Notes` (tutti a `AccessLevel.Read`) e
  `SpaceCalendars` (a `AccessLevel.Availability`, coerente con il minimo che la pagina stessa già
  richiede per essere visibile). **`SpaceUsage` e `InviteMember` esclusi deliberatamente**: la
  prima è una pagina da proprietario legata all'abbonamento, non un `ResourceKind` condiviso; la
  seconda è un'azione singola, non una vista ricorrente — includerle avrebbe richiesto piegare il
  modello del componente a un caso che il testo del lotto non cita esplicitamente.
- **Bug trovato e corretto tramite verifica dal vivo con due spazi reali**: la prima versione
  navigava con `NavigationManager.NavigateTo` senza `forceLoad`, e la pagina restava
  visivamente "congelata" sui dati del vecchio spazio nonostante l'URL cambiasse correttamente
  — confermato con un reload manuale della stessa pagina, che mostrava i dati giusti. Causa:
  tutte le pagine sotto `/spaces/{id}/...` caricano i propri dati in `OnInitializedAsync`, che
  Blazor **non rilancia** quando si naviga fra due URL che risolvono allo stesso componente
  instradato con un parametro diverso — solo `OnParametersSetAsync` (che il nuovo componente usa
  correttamente) viene richiamato. Corretto con `forceLoad: true` sulla navigazione dello
  switcher: un reload completo, ma comunque un solo passaggio invece dei due (dashboard → spazio)
  che sostituisce. Non ho toccato le pagine esistenti per spostare il caricamento dati in
  `OnParametersSetAsync` — sarebbe la correzione più organica ma tocca cinque pagine con logica
  già consolidata, fuori da quanto B15 chiedeva.

### B16 — Il 404 reale restituisce una pagina vuota ✅

- [x] **Dove**: `Program.cs` (pipeline HTTP — `UseWhen`/`UseStatusCodePagesWithReExecute`, nessun
  `UseRouting()` esplicito), `Routes.razor`, `NotFound.razor`
- **Perché**: scoperto verificando dal vivo B14 (non dal codice o da un test — un `curl` su un
  URL inesistente). `Program.cs` configura
  `UseStatusCodePagesWithReExecute("/not-found", ...)` per rieseguire la pipeline sulla pagina
  "not found" quando qualunque richiesta produce 404. In pratica **non succede**: un URL
  inesistente restituisce `HTTP 404` con `Content-Length: 0` — corpo completamente vuoto, non la
  pagina localizzata. Confermato via `git log` che questa configurazione esisteva già prima di
  questa sessione (introdotta in `6cf3d81`, poi condizionata a "non sotto `/hooks`" in `e1626f8`)
  — non è una regressione di questo lotto. Navigare direttamente a `/not-found` invece funziona
  perfettamente (200, pagina completa) — il problema è specificamente nella ri-esecuzione dopo
  un 404, non nella pagina in sé.
- **Ipotesi non verificata**: l'assenza di un `UseRouting()` esplicito lascia che ASP.NET Core lo
  inserisca automaticamente in un punto che potrebbe non essere coerente con dove serve rispetto a
  `UseWhen(...).UseStatusCodePagesWithReExecute(...)` — la correzione tipica per questo sintomo è
  assicurarsi che il middleware delle status code pages sia registrato prima di `UseRouting()`.
  Da verificare con un test mirato prima di cambiare la pipeline di produzione.
- **Cosa**: non corretto in questa sessione — tocca l'ordine dei middleware HTTP centrali
  (superficie sensibile, comune a *ogni* richiesta dell'app, non contenuta a una pagina), e non
  rientra nello scope letterale di B14 (che riguardava `NotFound.razor` come componente). Richiede
  una sessione dedicata con verifica attenta (idealmente un test di integrazione che chiami
  l'app reale e verifichi lo status/corpo di un URL inesistente, così la correzione non regredisce
  in futuro).
- **Fatto quando**: un URL inesistente restituisce la pagina "not found" localizzata (corpo non
  vuoto), status 404 mantenuto.
- **Causa trovata e corretta**: non era l'assenza di `UseRouting()` esplicito (provato per primo,
  non ha cambiato nulla). La causa reale è `UseWhen(...)` che avvolge
  `UseStatusCodePagesWithReExecute` — verificato empiricamente (build, avvio, richiesta,
  ripeti) che registrare il middleware **fuori** da `UseWhen` risolve immediatamente la
  ri-esecuzione, mentre incapsularlo in un branch `UseWhen` la rompe silenziosamente del tutto
  (nessuna traccia nei log, nemmeno con logging di routing a livello Debug). L'esclusione per
  `/hooks` — il motivo per cui `UseWhen` era lì — ora si ottiene con il meccanismo che ASP.NET
  Core offre proprio per questo: un middleware che, solo per i path sotto `/hooks`, imposta
  `context.Features.Get<IStatusCodePagesFeature>().Enabled = false` prima di proseguire.
  Verificato dal vivo entrambi i casi: un URL inesistente ora restituisce 404 con 8 KB di corpo
  (la pagina "Page not found" completa); una POST a `/hooks/telegram` senza il secret token
  resta un 400 con corpo di testo semplice, non riscritta nella pagina di errore. Nessun altro
  percorso (home, pricing, `/health`, login) risulta toccato dal riordino.

---

## Lotto C — Canali proattivi senza burocrazia

Sostituisce la Fase 3 (WhatsApp) come strada per raggiungere chi non usa Telegram. La motivazione
non è il costo dei template ma la Business Verification di Meta, impraticabile per un professionista
in regime forfettario senza iscrizione al registro imprese — vedi *Decisioni aperte*.

### C1 — `EmailChannel` e digest via email ✅

- [x] **Dove**: nuovo `Tessera.Channels/EmailChannel.cs`, `Jobs/DailyDigestJob.cs`,
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
- **Fatto**: `EmailChannel` (`Tessera.Channels`) è il terzo `IChannel`, registrato solo se
  `Email:ConnectionString`/`Email:SenderAddress` sono configurati (stesso pattern opzionale di
  `IEmailSender`). Aggiunta una quinta capability, `SupportsRealTimeNotifications` (default
  `true`, `false` solo per email) — così `NotificationAggregationFlushJob` (C3) esclude email dal
  fan-out in tempo reale: l'unico messaggio che riceve è il digest schedulato, non le notifiche
  aggregate delle azioni altrui (che per un canale senza tastiera inline userebbero comunque la
  finestra lunga da 5 minuti di C3, non "una volta al giorno" — vedi la nota aggiunta in
  `03-integrazioni.md`). `DigestFormatter` è stato scomposto in `BuildSections` (i dati
  strutturati) e `Format` (l'unione in testo semplice usata da Telegram/web, invariata bit per
  bit); `EmailChannel.SendDigestAsync` — chiamato direttamente da `DailyDigestJob`, non tramite
  `IChannel.SendTextAsync`, perché un'email ha bisogno di un oggetto, di sezioni impaginate e di
  un link di disiscrizione che l'interfaccia generica non prevede — usa `BuildSections` per
  produrre un'email HTML in tabelle con gli stessi valori esadecimali di `tokens.css` copiati a
  mano dello stesso `ForgotPassword.razor`. `User.EmailDigestEnabled` (nuova colonna, migrazione
  `AddEmailDigestEnabled`, applicata al DB condiviso) più un nuovo riquadro in `Profile.razor`
  ("Email digest") che, al primo opt-in, provisiona l'identità `ChannelIdentity` "email" tramite
  `LinkService.EnsureEmailIdentityAsync` (stesso schema di `EnsureWebIdentityAsync`, cercata per
  `UserId` non per indirizzo — l'email può cambiare). Link di disiscrizione firmato con l'API
  Data Protection di ASP.NET Core (`EmailUnsubscribeTokenService`, già disponibile senza
  configurazione aggiuntiva) — non scade mai di proposito: un link rimasto in una casella di
  posta per mesi deve funzionare comunque; il fallback in caso di rotazione delle chiavi è il
  riquadro in Profile, sempre disponibile. Nuovo endpoint anonimo `GET /email/unsubscribe` (stesso
  schema di `/set-culture`, `CultureEndpoints`) più una pagina di conferma `EmailUnsubscribed.razor`.
  Verificato dal vivo: toggle su Profile (persiste al reload, crea l'identità email), link di
  disiscrizione valido (spegne il flag, mostra conferma) e non valido (messaggio d'errore, nessun
  crash) — tutti con account e token reali generati tramite un endpoint diagnostico temporaneo
  poi rimosso. L'HTML dell'email è stato verificato visivamente rendendolo in un browser tramite
  lo stesso endpoint temporaneo. **Non verificato dal vivo**: la consegna reale di un'email, perché
  Azure Communication Services non è configurato in questo ambiente di sviluppo — nessun
  `Email:ConnectionString` disponibile per un invio reale.

### C2 — Web push sulla PWA ✅

- [x] **Dove**: `wwwroot/service-worker.js`, `wwwroot/manifest.webmanifest` (esistenti),
  `Tessera.Channels/WebChannel.cs`, `NotificationService`
- **Perché**: [06-roadmap.md](06-roadmap.md#canale-web-console-e-pwa) elenca "promemoria/digest
  proattivi anche sul canale web" fra le cose non fatte, e `WebChannel.Subscribe` oggi sostituisce
  la mailbox invece di affiancarla — quindi due schede aperte si escludono. Con il web push la
  console diventa un canale proattivo completo senza dipendere da nessun provider terzo.
- **Cosa**: sottoscrizione VAPID persistita per utente e per dispositivo, `WebChannel` che consegna
  al push quando la mailbox non è connessa, e `Subscribe` che diventa additivo.
- **Fatto quando**: un promemoria arriva come notifica di sistema sulla PWA installata a scheda
  chiusa.
- **Fatto**: nuova entità `PushSubscription` (`Tessera.Core.Users`, migrazione
  `AddPushSubscriptions`, applicata) dietro `IPushSubscriptionRepository`/`IPushSender`
  (`Tessera.Core.Abstractions`) — stesso schema `interfaccia in Core, implementazione in
  Data/Integrations` già usato per canali ed email, che evita a `Tessera.Channels` di dover
  referenziare `Tessera.Data` direttamente. `WebPushSender` (`Tessera.Integrations`) usa il
  pacchetto NuGet `WebPush` (porting ufficiale di web-push-libs, stessa scelta fatta per ACS
  invece di reimplementare RFC 8291/8292 a mano) e traduce i 404/410 del servizio push (l'utente
  ha disinstallato/revocato) in `PushSubscriptionGoneException`, così `WebChannel` può rimuovere
  la sottoscrizione morta senza dipendere dal tipo di eccezione specifico della libreria.
  `WebChannel.Post` ora è la lettura di "Subscribe diventa additivo": se non c'è una mailbox
  aperta (nessuna scheda con `/chat` collegata) prova il push invece di scartare il messaggio,
  usando `IServiceScopeFactory` per risolvere il repository scoped da una classe singleton —
  interpretazione deliberatamente conservativa: non tenta di risolvere anche il problema, distinto,
  delle schede multiple che si escludono a vicenda (quello resta l'altro "non fatto" di
  `06-roadmap.md`, fuori perimetro qui). Nuovo riquadro "Notifiche push" in `Settings.razor`
  (visibile solo se `WebPush:VapidPublicKey` è configurato, stesso pattern "degrada senza
  configurazione" di ogni altra integrazione) con `wwwroot/js/push.js` (richiesta permesso,
  `pushManager.subscribe`, POST a `/push/subscribe`) e gli endpoint corrispondenti
  (`RequireAuthorization().DisableAntiforgery()` — l'autenticazione via cookie `SameSite=Lax`
  basta da sola contro il CSRF su un `fetch()` che non può comunque presentare un token
  antiforgery, stesso ragionamento adottato altrove in questa sessione). Il service worker
  gestisce `push`/`notificationclick`. `AccountDeletionService` ripulisce anche
  `PushSubscriptions` alla cancellazione account (dimenticato nella prima stesura, corretto
  prima del commit).
  Verificato dal vivo end-to-end tranne un solo passo: con un account B senza mailbox aperta
  (visita `/chat` una volta per creare l'identità "web", poi se ne va) e una sottoscrizione
  push sintetica (endpoint/chiavi non reali) registrata tramite l'endpoint autenticato reale,
  l'invio di A ha fatto scattare correttamente il ramo di fallback push: il log server mostra
  `WebChannel.Post` risolvere la sottoscrizione e arrivare fino al passo di cifratura reale
  (`WebPush.Model.InvalidEncryptionDetailsException`, fallito solo perché la chiave P256DH finta
  non è una chiave EC valida — prova che il percorso codice/crittografia/VAPID viene eseguito
  correttamente fino in fondo). L'endpoint richiede davvero l'autenticazione (una richiesta senza
  cookie viene rediretta al login). **Non verificato dal vivo**: la sottoscrizione reale da un
  browser vero — Chromium headless in questo ambiente riporta il permesso "notifications" come
  concesso sia da `Notification.requestPermission()` sia da `navigator.permissions.query(...)`,
  ma `pushManager.subscribe()` fallisce comunque con "permission denied": una limitazione nota di
  Playwright/Chromium headless per questa combinazione di API, non del codice — non risolvibile
  in questa sessione senza un browser reale non headless con una vera sessione desktop.

### C3 — Aggregazione delle notifiche ✅

- [x] **Dove**: `Services/NotificationService.cs`
- **Perché**: dieci voci aggiunte alla lista producono dieci messaggi a ciascun altro membro.
  [04-costi.md](04-costi.md#mitigazioni-obbligatorie-prima-di-aprire-whatsapp) lo tratta come
  mitigazione obbligatoria per WhatsApp; senza WhatsApp resta buona igiene su Telegram e diventa
  **necessario** per l'email, dove dieci messaggi sono spam.
- **Cosa**: finestra di aggregazione di 60 s per (spazio × destinatario × tipo di evento), resa
  come un unico messaggio; su canali con `SupportsProactiveFree = false` o senza inline keyboard,
  finestra più lunga.
- **Fatto quando**: dieci aggiunte in un minuto producono una notifica per destinatario.
- **Dipende da**: conviene farlo prima o insieme a **C1**.
- **Fatto**: `NotificationService` non invia più subito — bufferizza ogni evento in
  `NotificationAggregationBuffer<TEvent>` (nuovo, `Tessera.Core/Notifications/`, puro e senza
  DB: la decisione di finestra è testabile senza database, per-CLAUDE.md), chiave
  `NotificationWindowKey(SpaceId, RecipientUserId, EventType)`. La durata si decide una volta,
  all'apertura della finestra: 60 s se il destinatario ha almeno un canale con
  `SupportsProactiveFree && SupportsInlineKeyboard`, altrimenti 5 minuti. Un nuovo
  `IScheduledJob` (`Jobs/NotificationAggregationFlushJob.cs`, ogni 30 s — il tick di
  `SchedulerWorker` è comunque il vero limite) scola le finestre pronte, risolve
  cultura/canali del destinatario al momento dell'invio (mai prima, per la stessa ragione per
  cui gli eventi restano solo fatti — [09-localizzazione.md](09-localizzazione.md)) e compone
  un unico messaggio: con un solo evento nella finestra il testo è identico a prima
  (`Notification.ShoppingItemAdded` ecc.); con più eventi dello stesso attore usa le nuove
  chiavi `Notification.ShoppingItems{Added,Checked}`/`Notification.ExpensesRecorded` (elenco
  articoli troncato a 5 con "e altri N"; spese aggregate come totale, senza categoria); con
  attori diversi nella stessa finestra usa le varianti `*MultipleActors`, senza nominare
  nessuno. `SchedulerWorker` è ora ospitato incondizionatamente (prima lo era solo con
  Telegram o un calendario configurati) perché `NotificationService` può bufferizzare anche da
  un'azione fatta solo sul canale web (`/chat`), senza Telegram — altrimenti le finestre non
  sarebbero mai state scolate in quella configurazione.
  Verificato dal vivo con due account reali in uno spazio condiviso: A ha inviato tre `add` di
  fila in `/chat`, B (con `/chat` aperta) ha ricevuto **un solo** messaggio aggregato
  ("… added 3 items: milk, eggs, bread") invece di tre. Lo stesso test ha anche fatto emergere
  un bug preesistente e indipendente da questa modifica — confermato riproducendolo anche su
  `main` prima di C3 (`git stash`), poi diagnosticato e risolto nella stessa sessione (vedi nota
  a parte più sotto): non un problema architetturale diffuso, ma due pagine specifiche
  (`InviteMember.razor`, `Spaces.razor`) che rendevano un bottone/form interattivo *prima* che
  `OnInitializedAsync` finisse di caricare i dati, permettendo un click abbastanza rapido da far
  partire una query sul `DbContext` scoped del circuito mentre quella di `OnInitializedAsync`
  era ancora in volo sullo stesso `DbContext` — da cui "Invalid operation. The connection is
  closed." o "A second operation was started on this context instance".
  `NotificationAggregationBuffer<TEvent>` ha 7 test unitari nuovi
  (`tests/Tessera.Core.Tests/Notifications/`), senza database.

**Bug trovato durante la verifica di C3, poi risolto (fuori dal perimetro di C3 in sé)**:
`InviteMember.razor` non aveva nessuna guardia di caricamento — a differenza di ogni altra
pagina sotto `/spaces/{id}/...`, che segue tutte lo stesso pattern `@if (space is null) {
spinner } else { ... tutto il resto, incluso ogni form ... }` — quindi il form con il bottone
"Generate link" (e, in assenza del controllo di ownership già completato, potenzialmente
visibile anche a un non-owner per una frazione di secondo) era cliccabile fin dal primissimo
render sincrono, ben prima che `OnInitializedAsync` finisse di validare spazio e permessi.
`Spaces.razor` aveva la guardia ma non la copriva tutta: proteggeva solo l'elenco degli spazi,
non il form "Crea nuovo spazio" subito sotto. In entrambi i casi un click abbastanza rapido
(verificato con Playwright, ma raggiungibile anche da un utente reale su una rete lenta che
clicca prima che la pagina abbia davvero finito di caricare) faceva partire l'handler del
bottone mentre `OnInitializedAsync` stava ancora usando lo stesso `DbContext` scoped del
circuito, con l'intero circuito Blazor che si rompeva ("An unhandled error has occurred.
Reload") invece di un errore contenuto. Diagnosticato aggiungendo un tracciamento temporaneo
per istanza di componente (rimosso) che ha mostrato `GenerateAsync` partire mentre
`OnInitializedAsync` era ancora sospesa sulla stessa istanza — non un problema di
prerendering/circuiti concorrenti come ipotizzato all'inizio. **Fix**: `InviteMember.razor` ha
ora un campo `loaded` (impostato solo dopo che spazio e ownership sono stati validati) con la
stessa guardia `@if (!loaded) { spinner } else { ... }` delle altre pagine; `Spaces.razor` ha
il form di creazione spostato dentro `@if (spaces is not null) { ... }`. Verificato dal vivo:
8 tentativi consecutivi il più rapidi possibile con Playwright contro `InviteMember`/`Spaces`
(prima: crash quasi sistematico) — zero crash dopo il fix. Non toccate le altre pagine sotto
`Components/Pages`: verificate una per una, seguono già tutte il pattern corretto (guardia che
copre l'intero contenuto interattivo), tranne `Home.razor`, la cui unica occorrenza residua di
"connection is closed" nei log è un caso diverso e innocuo — una query di `OnInitializedAsync`
abbandonata perché l'utente ha già navigato altrove, non un'azione dell'utente su quella stessa
pagina; non ha una guardia perché non ha bottoni che scrivono, solo link. `DetailedErrors: true`
aggiunto a `appsettings.Development.json` (solo Development) perché è stato indispensabile per
diagnosticare questo bug e servirà alla prossima occasione — senza, ASP.NET Core mostra solo
"An unhandled error has occurred" senza stack trace lato client.

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

### D3 — Telemetria di conversione ✅

- [x] **Dove**: `UsageService.TryRecordL3CallAsync`, `Pricing.razor`, `SpaceUsage.razor`
- **Perché**: si traccia `NotUnderstood` e la distribuzione del router, ma non un solo evento del
  percorso commerciale. Senza, non si sa dove si perdono le conversioni e ogni scelta di prezzo è
  cieca.
- **Cosa**: tre `TrackEvent` — limite di piano raggiunto (con quale limite), pagina prezzi vista,
  click su "sottoscrivi" — più l'esito del webhook.
- **Fatto quando**: l'imbuto limite → prezzi → click → attivazione è leggibile in Application
  Insights.
- **Fatto**: stesso schema già in uso per `NotUnderstood`/la distribuzione del router
  (`TelemetryClient?` opzionale, default `null` quando `ApplicationInsights:ConnectionString` non
  è configurato, Program.cs) — nessun nuovo meccanismo, solo quattro `TrackEvent` in più nei posti
  giusti. `UsageLimitReached` vive dentro `UsageService.TryRecordL3CallAsync` stesso (non nei tre
  punti di chiamata in `MessageProcessor.cs`): è l'unico posto che ha già piano, conteggio e
  spazio a disposizione, ed evita di tracciare la stessa cosa tre volte con dati diversi a seconda
  del chiamante. `PricingPageViewed` in `Pricing.razor.OnInitializedAsync` (una volta per
  caricamento pagina). `SubscribeClicked` in `SpaceUsage.razor.SubscribeAsync`, tracciato **prima**
  della chiamata a PayPal — è il punto in cui l'intento è espresso, non quello in cui l'esito è
  noto. `PayPalWebhookProcessed` dentro `PayPalSubscriptionService.HandleWebhookEventAsync`, con
  un `Outcome` per ramo (`Activated`/`Suspended`/`Cancelled`/`Expired`/`Updated`/`Renewed`/
  `Unhandled`/`UnknownSubscription`) — non nell'endpoint (`PayPalWebhookEndpoints.cs`), che non ha
  ancora risolto l'`eventType` in uno stato di dominio quando riceve la richiesta.
  `Tessera.Data` non aveva mai referenziato `Microsoft.ApplicationInsights` prima d'ora
  (`UsageService`/`PayPalSubscriptionService` sono le prime classi lì a prenderlo, stesso
  pacchetto/versione già usato da `Tessera.Ai`); nei componenti Razor (`Pricing.razor`,
  `SpaceUsage.razor`) `TelemetryClient` non è mai iniettato con `@inject` diretto — `@inject`
  userebbe `GetRequiredService` e romperebbe l'avvio quando Application Insights non è
  configurato, quindi si passa da `@inject IServiceProvider` con una proprietà calcolata
  (`TelemetryOrNull => ServiceProvider.GetService<TelemetryClient>()`), lo stesso schema già
  usato altrove per servizi opzionali (es. `Notes.razor`'s `AttachmentServiceOrNull`).
  **Non verificato dal vivo**: Application Insights non è configurato in questo ambiente di
  sviluppo, quindi il codice gira sempre nel ramo "assente" (`telemetry` sempre `null`); inoltre
  il firewall di Azure SQL ha bloccato l'accesso al database condiviso da questa sessione proprio
  mentre si tentava la verifica di `/pricing` (stesso IP già sbloccato in precedenza nella
  sessione, poi ribloccato — non chiaro il motivo, forse una regola temporanea scaduta), quindi
  nemmeno il ramo "assente" è stato controllato dal vivo end-to-end questa volta. Il pattern
  riusato (`telemetry?.TrackEvent(...)`, parametro opzionale con default `null`) è però lo stesso,
  identico, già in produzione per `MessageProcessed`/`NotUnderstood`/`RouterL1..3` in
  `MessageProcessor.cs` — non un meccanismo nuovo da convalidare.

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

### F1 — Ampliare il corpus del router ✅

- [x] **Dove**: `tests/Tessera.Ai.Tests/Routing/IntentRouterTests.cs`
- **Perché**: `CLAUDE.md` chiama questi test "i più preziosi del codebase" e avverte che
  regrediscono facilmente; [05-ottimizzazioni.md](05-ottimizzazioni.md#testare-il-router-è-essenziale)
  chiede un corpus crescente di frasi reali. **Correzione a questo stesso documento**: il "12 casi
  `InlineData` per 22 matcher" scritto qui era una lettura sbagliata — quei 12 erano le altre
  `[Theory]` del file (estrazione slot), non il `Corpus` vero, che già contava ~46 righe via
  `MemberData`. La copertura positiva era già ragionevole; quello che mancava davvero era la
  direzione negativa (frasi simili a un intento che un matcher non deve prendere).
- **Cosa**: portare il corpus a coprire ogni matcher con almeno una formulazione positiva e una
  negativa (deve andare a L3), incluse le frasi che `05` elenca come non coperte — "manca il pane",
  "finito il detersivo", "serve il caffè". Se quelle passano a L3 quando potrebbero essere gestite
  a L2, sono anche il lavoro di **F1b**: aggiungere i matcher corrispondenti.
- **Fatto quando**: ogni matcher ha copertura in entrambe le direzioni.
- **Fatto**: 23 nuove righe di corpus (117→142 test), verificate riga per riga contro la regex
  reale di ogni matcher prima di scriverle — non solo le frasi già citate in `05` (più "domani
  prendi anche le uova", "ah e il caffè", equivalenti EN), ma soprattutto **near-miss**: frasi che
  assomigliano a un intento ma non devono attivarlo ("quanto costa il latte?" ≠ "quanto ho speso",
  "ho aggiunto il latte per sbaglio" ≠ "aggiungi", "cancella l'appuntamento di domani" non è
  `shopping.clear`/`undo`). Aggiunto anche `Corpus_CoversEveryRegisteredMatcherWithAtLeastOnePositiveCase`
  — invariante meccanica che fallisce la build se un futuro matcher viene registrato senza una riga
  di corpus corrispondente, invece di restare una promessa manuale. Aggiunta la riga di
  regressione specifica per **A3** ("e a febbraio?" → sempre L3 per costruzione, nessun trigger
  word di nessun matcher).
- **F1b non fatto, deliberatamente**: non ho aggiunto nuovi matcher L2 per "manca/finito/serve X".
  A differenza di tutto il resto del lotto A (cache, filtro tool, health check — tutti interni,
  zero impatto sul comportamento visibile all'utente), un nuovo matcher L2 con confidenza 1.0
  cambia cosa il bot fa davvero: "manca poco all'arrivo" o "we're out of time" verrebbero aggiunti
  come voci sbagliate a una lista condivisa, un errore silenzioso mitigato solo da `/undo` — un
  compromesso rischio/beneficio diverso da una modifica di plumbing, e una decisione di prodotto
  che merita un via libera esplicito separato, non un'estensione implicita di "amplia il corpus".

### F2 — Test dei permessi ✅

- [x] **Dove**: `tests/Tessera.Core.Tests/Spaces/AccessPolicyTests.cs`,
  `Calendars/EffectiveCalendarLevelTests.cs`
- **Perché**: 68 righe per il modello a permessi granulari e 29 per il minimo a tre fattori.
  `CLAUDE.md`: "un errore lì fa fuoriuscire dati fra i membri di uno spazio".
- **Cosa**: tabella esaustiva livello richiesto × livello concesso × tipo di risorsa, più i casi
  limite del minimo `min(ProviderRole, ShareLevel, MembershipPermission)` (regola 15) e l'ex
  membro.
- **Fatto quando**: ogni combinazione di `AccessLevel` e `ResourceKind` è coperta.
- **Fatto**: `AccessPolicyTests.cs` (4→13 test) — matrice 5×5 di `AccessLevel` (concesso ×
  richiesto) generata via `MemberData`, non trascritta a mano, per non correre il rischio di
  copiare lo stesso errore nel test e nel codice; test esplicito che una permission su una
  risorsa non fuoriesce su nessun'altra (le 6 rimanenti, con `Admin` sulla prima — il caso più
  aggressivo possibile); test che più permission sulla stessa membership restano indipendenti;
  owner esaustivo su tutti i 7 `ResourceKind` × 5 `AccessLevel`. **L'ex membro non ha un test
  dedicato**: da questa classe è indistinguibile da "nessuna membership" — un membro uscito non
  ha più una riga `Membership` (viene archiviato in `MembershipArchive`, docs/02-modello-dati.md),
  quindi ricade nel path già coperto da `CanAsync_ReturnsFalse_WhenUserHasNoMembership`; ho
  aggiunto solo un test che lo rende esplicito invece di inventare un secondo test ridondante.
  `EffectiveCalendarLevelTests.cs` (7→16 test): oltre agli scenari combinati già presenti, tre
  nuove theory isolano ciascuno dei tre vincoli (bloccando gli altri due al valore "nessun
  vincolo") con il valore atteso dichiarato dal commento di dominio di `ProviderAccessRole`, non
  ri-derivato dallo switch di produzione — altrimenti un'inversione nel codice si sarebbe
  propagata identica nel test. Due test difensivi su valori enum non definiti (`(ProviderAccessRole)0`).

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
| 2 | **A4, A5** ✅ | Correttezza e operabilità: il messaggio perso è un difetto silenzioso, e si sistema in poche ore |
| 3 | **A3** + **F1** ✅ | Lo storico conversazionale è la retention; il corpus del router lo protegge dalle regressioni |
| 4 | **B1, B2, B3, B4, B6, B7** ✅ | Sei interventi visibili e circoscritti. B3 è il più importante: è la pagina a uso quotidiano |
| 5 | **B5, B8, B9, B10, B11, B14** ✅ | Accessibilità, riscontro, tono. B8 è solo riscrittura di risorse. Scoperte due questioni non previste: B5's skip link è oscurato da `FocusOnNavigate` pre-esistente; il 404 reale (`UseStatusCodePagesWithReExecute`) è rotto da prima di questa sessione |
| 6 | **F2** ✅ | I permessi, prima di aggiungere superficie che li usa — fatto fuori ordine, su richiesta esplicita, prima dei passi 4-5 (lotto B) |
| 7 | **C3, C1** ✅ | Aggregazione e poi email: il canale che sostituisce WhatsApp |
| 8 | **D3** ✅, poi decisioni aperte 2 e 3, poi **D1, D4, D2, D5** | La telemetria prima del riassetto: si decide su dati, non a memoria |
| 9 | **E3, E2, E4, E1** | Export e garanzie sono quasi gratis; la voce merita di stare dopo perché tocca la pipeline |
| 10 | **B12, B13, B15**, **F3**, **F4** | Manutenibilità e rifiniture, quando i pattern si sono consolidati |
| 11 | **C2** ✅ (fatto fuori ordine insieme a C1/C3, su richiesta esplicita — chiude tutto il lotto C), **E5, E6, E7** | Il resto, senza urgenza |

**G1-G5 vanno fatti subito**, fuori da questa scaletta: sono divergenze già accertate fra
documentazione e realtà, e finché restano aperte ogni rilettura di `docs/` riparte da premesse
sbagliate. **G6** e **G7** seguono i lotti che li generano.
