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
      WhatsAppWebhook.cs      ← fase 3
    Public/                   ← homepage, privacy policy, termini (necessari per OAuth review)
    Services/
      MessageQueue.cs         ← Channel<T> in memoria
      MessageProcessor.cs     ← BackgroundService, consumer reattivo
      SchedulerWorker.cs      ← BackgroundService, lavoro temporizzato
    Jobs/                     ← RemindersDueJob, DailyDigestJob, RecurringExpenseJob
  Tessera.Core/             ← domain puro, zero dipendenze da infrastruttura
    Spaces/                   ← Space, Membership, Role, ResourceKind
    Shopping/                 ← ShoppingList, ShoppingItem
    Expenses/                 ← Expense, Category, RecurringExpense, Budget
    Reminders/                ← Reminder, RecurrenceRule
    Conversations/            ← intent, sessione, contesto
    Notifications/            ← eventi di dominio strutturati (vedi 09)
    Resources/                ← Messages.resx, Messages.it.resx
    Abstractions/             ← interfacce dei repository e dei servizi
  Tessera.Channels/         ← IChannel, TelegramChannel, WhatsAppChannel
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

L'unico modo per non riscrivere la pipeline quando arriva WhatsApp.

```csharp
public interface IChannel
{
    string Name { get; }                     // "telegram" | "whatsapp"
    Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct);
    Task SendChoicesAsync(ChannelAddress to, string text,
                          IReadOnlyList<Choice> choices, CancellationToken ct);
    ChannelCapabilities Capabilities { get; }
}

public record ChannelCapabilities(
    bool SupportsGroups,          // Telegram: true — WhatsApp Cloud API: false
    bool SupportsInlineKeyboard,  // Telegram: true — WhatsApp: solo reply/list button
    bool SupportsProactiveFree,   // Telegram: true — WhatsApp: solo entro 24h
    bool SupportsDeepLinkPayload  // Telegram: true (/start <token>) — WhatsApp: false
);
```

Le differenze fra canali non vanno nascoste dietro un'astrazione finta: sono esposte via `Capabilities` e la logica applicativa si adatta. Un promemoria proattivo su Telegram è un messaggio libero, su WhatsApp è un template a pagamento — la pipeline deve saperlo.

Il messaggio in ingresso viene normalizzato subito:

```csharp
public record InboundMessage(
    string ChannelName,
    string ExternalChatId,        // chat_id Telegram / wa_id WhatsApp
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

Oltre al consumer della coda dei messaggi (reattivo), serve un worker **temporizzato** per il lavoro proattivo: promemoria scaduti, digest quotidiano, generazione delle spese ricorrenti, avvisi di budget.

```csharp
public interface IScheduledJob
{
    string Name { get; }
    TimeSpan Interval { get; }
    Task RunAsync(CancellationToken ct);
}

// RemindersDueJob      — ogni minuto
// DailyDigestJob       — ogni 15 minuti (l'ora locale del digest varia per fuso)
// RecurringExpenseJob  — ogni ora
// BudgetAlertJob       — dopo ogni spesa, o ogni ora
```

Due vincoli non ovvi:

**Il digest non ha un'ora unica.** "Alle 8 del mattino" è un istante diverso per utente a seconda del `TimeZoneId`. Il job gira spesso e seleziona gli utenti la cui ora locale corrisponde alla preferenza, invece di girare una volta al giorno a un'ora fissa.

**Ogni invio deve essere idempotente.** Un riavvio dell'app durante il ciclo non deve produrre notifiche duplicate. Da qui i campi `Reminder.NotifiedAt`, `RecurringExpense.LastGeneratedFor`, `Budget.LastAlertedFor` descritti in [02-modello-dati.md](02-modello-dati.md): il worker controlla lo stato persistito, non un flag in memoria.

Nell'MVP è un `BackgroundService` con un timer nella stessa app. Con più istanze serve un lease distribuito (blob lease o una tabella di lock), altrimenti due istanze inviano lo stesso digest — ed è un altro dei motivi per cui la Fase 1 resta a istanza singola.

## Autenticazione: due mondi nella stessa app

| Superficie | Meccanismo |
|---|---|
| Console web | Cookie auth (**ASP.NET Core Identity**) |
| `/hooks/telegram` | `AllowAnonymous` + header `X-Telegram-Bot-Api-Secret-Token` |
| `/hooks/whatsapp` | `AllowAnonymous` + HMAC SHA-256 su `X-Hub-Signature-256` |
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

Con un `Channel<T>` in memoria e una sola istanza, un riavvio o un deploy perde i messaggi accodati e non ancora processati. Accettabile in Fase 1. È il primo motivo concreto per introdurre **Azure Service Bus**, insieme alla necessità di scalare oltre una singola istanza (con più istanze, la coda in memoria significa che ogni istanza vede solo i propri messaggi — funziona, ma perde la possibilità di ritentare il lavoro di un'istanza caduta).

L'interfaccia `MessageQueue` è pensata per rendere quella sostituzione una modifica di una riga in `Program.cs`.

## Perché App Service e non Container Apps

Entrambi funzionano. App Service è la scelta giusta qui perché:

- Deploy diretto da `dotnet publish` o GitHub Actions senza Dockerfile né registry
- Costo fisso e prevedibile, nessun ragionamento su replica minima e scale-to-zero
- Certificati gestiti e custom domain con configurazione minima
- Il debug in produzione (log stream, console Kudu) è immediato

Container Apps ha senso se in futuro servono più servizi indipendenti o KEDA su lunghezza coda. Non ora.
