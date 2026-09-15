# 01 — Architettura

## Scelta di fondo: host unico

Console web e webhook dei bot girano nella **stessa applicazione ASP.NET Core**, deployata su una singola Azure Web App.

Perché:

- Un solo deploy, un solo slot, un solo certificato, un solo `dotnet run` in locale
- La console non aggiunge costo infrastrutturale: sono altri controller e altre pagine Razor
- La OAuth verification di Google richiede comunque una homepage pubblica e una privacy policy sul dominio — se quelle pagine devono esistere, tanto vale che siano la console vera

I confini interni restano però separati per progetto, così che una futura scissione (webhook come app dedicata) richieda solo di spostare la composizione in `Program.cs`, non di riscrivere logica.

## Struttura della soluzione

```
src/
  Tessera.Web/              ← unico progetto host (ASP.NET Core + Blazor)
    Program.cs
    Components/
      Pages/                  ← console: dashboard, spazi, membri, account collegati
      Layout/
    Endpoints/
      TelegramWebhook.cs
      PayPalWebhookEndpoints.cs
      PushEndpoints.cs
    Public/                   ← homepage, privacy policy, termini (necessari per OAuth review)
    Services/
      MessageQueue.cs         ← Channel<T> in memoria
      MessageProcessor.cs     ← BackgroundService, consumer reattivo
      SchedulerWorker.cs      ← BackgroundService, lavoro temporizzato
    Jobs/                     ← RemindersDueJob, DailyDigestJob, RecurringExpenseJob,
                                 PendingMessageRecoveryJob, NotificationAggregationFlushJob, e altri
                                 (vedi "Il secondo worker" più sotto per l'elenco completo)
  Tessera.Core/             ← domain puro, zero dipendenze da infrastruttura
    Spaces/                   ← Space, Membership, Role, ResourceKind
    Shopping/                 ← ShoppingList, ShoppingItem
    Expenses/                 ← Expense, Category, RecurringExpense, Budget
    Reminders/                ← Reminder, RecurrenceRule
    Conversations/            ← intent, sessione, contesto
    Notifications/            ← eventi di dominio strutturati (vedi 09)
    Resources/                ← Messages.resx, Messages.it.resx
    Abstractions/             ← interfacce dei repository e dei servizi
  Tessera.Channels/         ← IChannel, IChannelRegistry, TelegramChannel, WebChannel, EmailChannel
  Tessera.Integrations/     ← GraphCalendarClient, GoogleCalendarClient
  Tessera.Ai/               ← router di intent, client Azure OpenAI, tool schema
  Tessera.Data/             ← EF Core: DbContext, configurazioni, migrations
tests/
  Tessera.Core.Tests/
  Tessera.Ai.Tests/         ← test del router: fondamentali, vedi 05
docs/
```

`Tessera.Core` non referenzia nulla di infrastrutturale. Le regole sui permessi vivono lì e sono testabili senza database.

## Astrazione del canale

L'unico modo per non riscrivere la pipeline ogni volta che si aggiunge un canale — finora Telegram, web e email, in quest'ordine.

```csharp
public interface IChannel
{
    string Name { get; }                     // "telegram" | "web" | "email"
    Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct);
    Task SendChoicesAsync(ChannelAddress to, string text,
                          IReadOnlyList<Choice> choices, CancellationToken ct);
    ChannelCapabilities Capabilities { get; }
    // Più altri: SendGroupedChoicesAsync, EditListMessageAsync, SendPhotoAsync,
    // SendDocumentAsync, DownloadMediaAsync — omessi qui, non cambiano la forma dell'astrazione.
}

public record ChannelCapabilities(
    bool SupportsGroups,                    // Telegram: true — web/email: false
    bool SupportsInlineKeyboard,            // Telegram: true — web: true — email: false
    bool SupportsProactiveFree,             // Telegram/web/email: true (nessun costo per messaggio proattivo su nessuno dei tre oggi)
    bool SupportsDeepLinkPayload,           // Telegram: true (/start <token>) — web/email: false
    bool SupportsRealTimeNotifications = true  // true per tutti tranne email — vedi sotto
);
```

Un canale futuro con vincoli reali (un costo per messaggio proattivo, niente gruppi) userebbe gli stessi flag per dichiararlo, senza toccare `MessageProcessor`/`NotificationService` — le differenze fra canali si espongono via `Capabilities`, non si nascondono dietro un'astrazione finta.

### Canale Web (console) e `IChannelRegistry`

Un secondo canale, oltre a Telegram: la pagina `/chat` della console stessa, per chi non ha Telegram, accede da un altro dispositivo, o vuole solo provare l'assistente. Nessun provider esterno, nessun webhook — l'invio non è una chiamata HTTP in uscita ma la scrittura in una mailbox in memoria per utente, che la pagina Blazor legge in streaming finché resta aperta (con fallback a web push se la mailbox non ha un ascoltatore attivo — vedi sotto). `WebChannel.Capabilities` ha `SupportsGroups: false` (nessun concetto di gruppo in console) e `SupportsDeepLinkPayload: false` (l'utente è già autenticato, non serve un token `/start`).

L'identità segue lo stesso schema di Telegram — `ChannelIdentity` con `ChannelName = "web"` — ma **auto-provisionata** invece che tramite `LinkToken`: chi è già loggato in console non ha bisogno di un flusso di collegamento, `LinkService.EnsureWebIdentityAsync` crea la riga idempotentemente al primo caricamento di `/chat`. Questo evita qualunque ramo speciale nel resto della pipeline: `MessageProcessor` risolve l'utente da `ChannelIdentity` esattamente come per Telegram.

Aggiungere un secondo canale ha reso evidente un problema che esisteva già in nuce: `MessageProcessor`, `NotificationService` e i job proattivi (`RemindersDueJob` e simili) iniettavano un singolo `IChannel` — corretto quando ne esisteva uno solo, ma con più registrazioni la DI di .NET risolve silenziosamente l'ultima registrata invece di segnalare un errore. `IChannelRegistry` (in `Tessera.Core.Channels`, implementato in `Tessera.Channels`) risolve il canale giusto per `ChannelName` in ognuno di questi punti.

**v1 vs v2.** La v1 è solo testo: nessun `IBrowserFile` in ingresso, `WebChannel.DownloadMediaAsync` non aveva nulla da scaricare. La v2 aggiunge gli allegati: la pagina legge i byte del file scelto nel browser (già disponibili localmente, a differenza del `file_id` di Telegram che va risolto con una chiamata al provider) e li deposita in `WebChannel.StageUpload`, che restituisce un id usato come `InboundMedia.FileId` — `DownloadMediaAsync` lo consuma una sola volta quando `MessageProcessor` lo richiede. Stessa pipeline di scontrini/note-con-foto di Telegram, zero rami speciali. La v3 aggiunge il web push (docs/13-piano-miglioramenti.md, C2): quando `WebChannel.Post` non trova una mailbox aperta, prova una sottoscrizione VAPID persistita per dispositivo (`Tessera.Core.Users.PushSubscription`) invece di scartare il messaggio.

**Limite noto**: una sola scheda per utente riceve gli aggiornamenti live in streaming (`WebChannel.Subscribe` sostituisce la mailbox precedente anziché accodarsi); più schede aperte contemporaneamente sullo stesso account non è un caso gestito — il push v3 attenua il problema (raggiunge comunque il dispositivo), non lo risolve (non sincronizza fra schede).

### `EmailChannel` — il terzo canale, un caso a parte

`EmailChannel` (`Tessera.Channels`, usa `Azure.Communication.Email.EmailClient` — non un `HttpClient` fatto in casa come Telegram/PayPal/calendari, la firma HMAC per-richiesta della connection string non vale la pena reimplementarla) è il primo canale con `SupportsInlineKeyboard: false` e `SupportsRealTimeNotifications: false`: `SendChoicesAsync`/`SendGroupedChoicesAsync` lanciano `NotSupportedException` (una email non può rispondere a un tap), e il fan-out in tempo reale di `NotificationService` lo salta sempre — l'unico messaggio che email riceve è il digest quotidiano schedulato (`DailyDigestJob`), tramite `EmailChannel.SendDigestAsync` (subject + sezioni HTML + link di disiscrizione, non lo stesso "manda questo testo" di `SendTextAsync`). Dettagli su cosa questo implica per la localizzazione in [09-localizzazione.md](09-localizzazione.md#notifiche-in-tempo-reale-vs-digest-per-canale), sul costo in [04-costi.md](04-costi.md). Registrato in `Program.cs` solo se `Email:ConnectionString`/`Email:SenderAddress` sono configurati (stesso pattern opzionale di ogni altra integrazione) — senza, il digest semplicemente non raggiunge nessuno via email, Telegram e web restano invariati.

Le differenze fra canali non vanno nascoste dietro un'astrazione finta: sono esposte via `Capabilities` e la logica applicativa si adatta. Un promemoria proattivo su Telegram è un messaggio libero e in tempo reale; su email è confinato al digest quotidiano — la pipeline deve saperlo.

Il messaggio in ingresso viene normalizzato subito:

```csharp
public record InboundMessage(
    string ChannelName,
    string ExternalChatId,        // chat_id Telegram / mailbox key web / indirizzo email
    string? ExternalUserId,       // mittente nel gruppo
    string? Text,
    IReadOnlyList<InboundMedia> Media,
    string ProviderMessageId,     // per idempotenza
    DateTimeOffset SentAt);
```

## Pipeline di elaborazione

```
webhook HTTP
   │  validazione firma (secret token / HMAC)
   │  deduplica su ProviderMessageId
   ├──► 200 OK immediato
   │
   └──► Channel<InboundMessage> (in memoria)
             │
        BackgroundService
             ├─ risoluzione identità: ExternalChatId → Utente + Spazio
             ├─ impostazione cultura dell'utente (vedi 09 — non c'è HTTP context qui)
             ├─ router di intent (vedi 05)
             │    ├─ fast path deterministico ──► handler di dominio
             │    └─ fallback LLM ──► function calling ──► handler di dominio
             ├─ persistenza (EF Core)
             └─ risposta via IChannel + eventi di notifica resi per destinatario
```

Due passaggi della pipeline meritano attenzione e sono trattati altrove:

- La **risoluzione dello spazio** in chat privata non è banale se l'utente appartiene a più spazi: la catena di precedenza è in [02-modello-dati.md](02-modello-dati.md).
- La **cultura** va impostata esplicitamente qui, perché nel worker non esiste un HTTP context e nessun middleware l'ha fatto. Ometterlo produce un bug silenzioso: tutto esce in inglese. Vedi [09-localizzazione.md](09-localizzazione.md).

### Rispondere subito è obbligatorio

Telegram ritenta la consegna se non riceve `200 OK` entro pochi secondi, e il risultato sono messaggi duplicati. Il webhook accoda e ritorna; l'elaborazione (che include chiamate LLM da 1-3 secondi) avviene fuori dalla richiesta HTTP.

```csharp
app.MapPost("/hooks/telegram", async (Update update, MessageQueue queue) =>
{
    await queue.EnqueueAsync(update.ToInbound());
    return Results.Ok();
}).AllowAnonymous().AddEndpointFilter<TelegramSecretTokenFilter>();
```

### Idempotenza

Ogni messaggio processato viene registrato con `(ChannelName, ProviderMessageId)` come chiave univoca. Un retry del provider trova il record e viene scartato. Senza questo, ogni timeout diventa una voce duplicata nella lista della spesa.

## Il secondo worker: lavoro temporizzato

Oltre al consumer della coda dei messaggi (reattivo), serve un worker **temporizzato** per il lavoro proattivo: promemoria scaduti, digest quotidiano, generazione delle spese ricorrenti, recupero di code perse. Gli avvisi di budget restano sincroni (`AppendBudgetAlertsAsync`, subito dopo la registrazione di una spesa), non un job a sé.

```csharp
public interface IScheduledJob
{
    string Name { get; }
    TimeSpan Interval { get; }
    Task RunAsync(CancellationToken ct);
}
```

`SchedulerWorker` risolve `IEnumerable<IScheduledJob>` una sola volta alla costruzione; ogni job apre il proprio scope a ogni esecuzione. Registrati oggi (`Program.cs`), ciascuno dietro il proprio gate opzionale dove ne ha uno:

| Job | Intervallo | Gate |
|---|---|---|
| `NotificationAggregationFlushJob` | secondo la finestra di aggregazione (docs/13-piano-miglioramenti.md, C3) | nessuno — deve girare anche senza Telegram configurato |
| `RemindersDueJob` | ogni minuto | Telegram |
| `DailyDigestJob` | ogni 15 minuti (l'ora locale del digest varia per fuso) | Telegram |
| `RecurringExpenseJob` | ogni ora | Telegram |
| `PendingMessageRecoveryJob` | ogni 5 minuti, dalla prima esecuzione | Telegram — vedi "Vincolo noto: coda in memoria" sotto |
| `RefreshCalendarListJob` | periodico | integrazione calendario |
| `CalendarReminderJob` | periodico | calendario + Telegram |
| `CalendarToListSuggestionJob` | periodico | calendario + Telegram |
| `ProcessedMessagePurgeJob` | periodico | nessuno |

Due vincoli non ovvi:

**Il digest non ha un'ora unica.** "Alle 8 del mattino" è un istante diverso per utente a seconda del `TimeZoneId`. Il job gira spesso e seleziona gli utenti la cui ora locale corrisponde alla preferenza, invece di girare una volta al giorno a un'ora fissa.

**Ogni invio deve essere idempotente.** Un riavvio dell'app durante il ciclo non deve produrre notifiche duplicate. Da qui i campi `Reminder.NotifiedAt`, `RecurringExpense.LastGeneratedFor`, `Budget.LastAlertedFor` descritti in [02-modello-dati.md](02-modello-dati.md): il worker controlla lo stato persistito, non un flag in memoria.

Nell'MVP è un `BackgroundService` con un timer nella stessa app. Con più istanze serve un lease distribuito (blob lease o una tabella di lock), altrimenti due istanze inviano lo stesso digest — ed è un altro dei motivi per cui la Fase 1 resta a istanza singola.

## Autenticazione: due mondi nella stessa app

| Superficie | Meccanismo |
|---|---|
| Console web | Cookie auth (**ASP.NET Core Identity**) |
| `/hooks/telegram` | `AllowAnonymous` + header `X-Telegram-Bot-Api-Secret-Token` |
| `/hooks/paypal` | `AllowAnonymous` + verifica firma via chiamata a PayPal (`POST /v1/notifications/verify-webhook-signature`, non un HMAC locale — vedi [03-integrazioni.md](03-integrazioni.md#webhook-e-validazione-della-firma)) |
| Pagine pubbliche | Anonime (homepage, privacy, termini) |

I webhook **non devono** passare per il cookie della console: non hanno un utente loggato. Sono due endpoint filter, non due applicazioni.

### Perché ASP.NET Core Identity e non Entra External ID

Decisione presa: **ASP.NET Core Identity**, non Entra External ID.

Il requisito di prodotto è login con email/password fin dal giorno uno, con provider social (Google, Microsoft) aggiunti in una fase successiva senza richiedere un ridisegno. Identity copre esattamente questo: gli account locali (`AspNetUsers`) e i login esterni (`AspNetUserLogins`) condividono lo stesso utente fin dallo schema — aggiungere un provider social più avanti è configurazione (`AddGoogle`, `AddMicrosoftAccount` in `Program.cs` più una pagina di callback), non una migrazione dei dati.

Entra External ID farebbe la stessa cosa, ma delegando l'intera identità a un tenant Azure esterno fin dalla Fase 1 — in contrasto con il principio "zero OAuth in Fase 1" della roadmap (vedi [06-roadmap.md](06-roadmap.md)), e con un costo/complessità di setup non giustificato per un MVP a singolo sviluppatore.

**Da non confondere con altri due flussi OAuth-simili già nello schema** (vedi [02-modello-dati.md](02-modello-dati.md)):

| Flusso | Scopo | Entità |
|---|---|---|
| Login social (Identity, futuro) | Autenticarsi in console | `AspNetUserLogins` (di Identity) |
| `LinkedAccount` | Accesso al Calendario (Fase 2) | `LinkedAccount` |
| `ChannelIdentity` | Collegare Telegram/WhatsApp | `ChannelIdentity` |

Un utente che fa login con Google **non** ha per ciò autorizzato l'accesso al suo Google Calendar: sono consensi distinti, con scope distinti, anche se il provider è lo stesso.

L'account tecnico di Identity (`ApplicationUser`, in `Tessera.Data`) resta separato dall'entità di dominio `User` (in `Tessera.Core`, senza dipendenze da Identity): stesso `Guid Id`, creati insieme alla registrazione, ma `Tessera.Core` non referenzia mai tipi di Identity — vedi la regola "zero dipendenze da infrastruttura" più sopra.

## Deploy su Azure

```
Azure Web App (App Service)
├── Basic B1 o Standard S1, always-on attivo
├── Managed Identity ──► Key Vault (segreti, refresh token cifrati)
├── Connection string ──► Azure SQL (o Cosmos DB)
├── Application Insights
└── Custom domain + certificato gestito (necessario per OAuth review)
```

**Always-on è obbligatorio.** Con il piano Free/Shared o senza always-on l'app va in idle e il primo webhook dopo l'inattività si perde o va in timeout: Telegram ritenta, ma l'esperienza è un ritardo di secondi sul primo messaggio della giornata. B1 è il minimo sensato.

**Deployment slot**: uno slot `staging` su Standard S1 permette swap senza perdere messaggi in volo. Su B1 non è disponibile — accettabile nell'MVP, il downtime di un deploy è di secondi e Telegram ritenta.

### Vincolo noto: coda in memoria

Con un `Channel<T>` in memoria e una sola istanza, un riavvio o un deploy fra "webhook ha risposto 200" e "il messaggio è stato processato" perde quel messaggio dalla coda — e Telegram non lo ri-consegna, avendo già ricevuto 200. Accettabile in Fase 1, ma non lasciato senza rete: `TelegramUpdateIngestor` scrive una riga `ProcessedMessage` (con l'`InboundMessage` serializzato in `PayloadJson`) **prima** di accodare, e `MessageProcessor` valorizza `CompletedAt` solo a lavoro finito. `PendingMessageRecoveryJob` (docs/13-piano-miglioramenti.md, A4) gira ogni 5 minuti — e alla primissima esecuzione dopo l'avvio, entro un minuto circa — e ri-accoda ogni riga `CompletedAt == null` più vecchia di una soglia di un minuto (il margine per un messaggio ancora legittimamente in corso sulla stessa istanza). Chiude la finestra di perdita silenziosa, non elimina il vincolo di fondo: con più istanze la coda in memoria significa che ogni istanza vede solo i propri messaggi — funziona, ma perde la possibilità di ritentare il lavoro di un'istanza caduta senza un simile meccanismo di recovery per istanza. È il primo motivo concreto per introdurre **Azure Service Bus**, insieme alla necessità di scalare oltre una singola istanza.

L'interfaccia `MessageQueue` è pensata per rendere quella sostituzione una modifica di una riga in `Program.cs`.

## Perché App Service e non Container Apps

Entrambi funzionano. App Service è la scelta giusta qui perché:

- Deploy diretto da `dotnet publish` o GitHub Actions senza Dockerfile né registry
- Costo fisso e prevedibile, nessun ragionamento su replica minima e scale-to-zero
- Certificati gestiti e custom domain con configurazione minima
- Il debug in produzione (log stream, console Kudu) è immediato

Container Apps ha senso se in futuro servono più servizi indipendenti o KEDA su lunghezza coda. Non ora.
