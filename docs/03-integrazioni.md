# 03 — Integrazioni

> Le informazioni su API di terze parti in questo documento riflettono lo stato noto a maggio 2026. Meta, Google e Amazon modificano policy e disponibilità con frequenza: **verificare sulla documentazione ufficiale prima di impegnare tempo di sviluppo** su ciascuna integrazione.

## Telegram — canale primario

Il canale più semplice di tutti, e il motivo per cui la Fase 1 parte da qui.

| Aspetto | Valore |
|---|---|
| Costo | Zero |
| Verifica / approvazione | Nessuna |
| Setup | Un messaggio a [@BotFather](https://t.me/BotFather) |
| Gruppi | Supportati nativamente |
| Messaggi proattivi | Liberi e gratuiti |
| Limiti | ~30 messaggi/sec in broadcast, ~20 msg/min per gruppo |
| SDK .NET | `Telegram.Bot` su NuGet, maturo |

### Webhook e sicurezza

Al momento della registrazione del webhook si imposta un secret token che Telegram invierà in ogni richiesta nell'header `X-Telegram-Bot-Api-Secret-Token`. È l'unico meccanismo di autenticazione dell'endpoint.

```csharp
public class TelegramSecretTokenFilter(IConfiguration config) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var expected = config["Telegram:WebhookSecret"];
        var received = ctx.HttpContext.Request
            .Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected!), Encoding.UTF8.GetBytes(received)))
            return Results.Unauthorized();

        return await next(ctx);
    }
}
```

Confronto a tempo costante, non `==`: è un confronto di segreti.

Consiglio operativo: usare un path del webhook non indovinabile (`/hooks/telegram/{guid}`) come secondo strato. Non è sicurezza vera, ma riduce il rumore da scanner.

### Migrazione gruppo → supergruppo

Il gotcha che rompe gli spazi legati a un gruppo, mesi dopo il setup.

Quando un gruppo diventa supergruppo — aggiungendo membri, cambiando permessi, abilitando lo storico per i nuovi iscritti — **il `chat_id` cambia**. Telegram lo comunica in due forme, e conviene gestirle entrambe perché arrivano in momenti diversi:

```csharp
// Forma 1: messaggio di servizio nel gruppo VECCHIO
//          update.Message.MigrateToChatId  (il nuovo id)
// Forma 2: messaggio di servizio nel gruppo NUOVO
//          update.Message.MigrateFromChatId (il vecchio id)

if (msg.MigrateToChatId is { } newId)
    await spaces.RemapGroupChatAsync(oldChatId: msg.Chat.Id.ToString(),
                                     newChatId: newId.ToString(), ct);

if (msg.MigrateFromChatId is { } oldId)
    await spaces.RemapGroupChatAsync(oldChatId: oldId.ToString(),
                                     newChatId: msg.Chat.Id.ToString(), ct);
```

`RemapGroupChatAsync` deve essere **idempotente**: entrambe le forme possono arrivare per la stessa migrazione, e la seconda non deve fallire né duplicare. Sposta `GroupChatId` in `PreviousGroupChatId` e scrive il nuovo, oppure non fa nulla se il nuovo id è già quello corretto.

Va gestito anche il caso in cui la migrazione avviene **mentre il bot è offline**: gli update di servizio si perdono. Rete di sicurezza: se arriva un messaggio da un `chat_id` di gruppo sconosciuto e il bot è membro di quel gruppo, vale la pena loggarlo come anomalia invece di ignorarlo silenziosamente. Un comando `/link` nel gruppo che ri-associa lo spazio è il rimedio manuale, e va previsto perché prima o poi serve.

Altri eventi di ciclo di vita del gruppo da gestire nello stesso punto:

| Evento | Azione |
|---|---|
| Bot rimosso dal gruppo (`my_chat_member` → `left`/`kicked`) | Azzerare `GroupChatId`, **non** eliminare lo spazio né i dati |
| Bot aggiunto a un gruppo | Messaggio di benvenuto e proposta di associare o creare uno spazio (vedi [10](10-conversazione.md)) |
| Gruppo eliminato | Nessun update affidabile: lo spazio resta orfano, gestibile dalla console |

La prima riga è importante: rimuovere il bot da un gruppo non deve cancellare la lista della spesa. Il bot può essere rimosso per errore e riaggiunto, e i dati devono sopravvivere.

### Gruppi: privacy mode

Per default il bot in un gruppo riceve **solo** i messaggi che iniziano con `/` o che lo menzionano. Per intercettare "aggiungi il latte" detto liberamente nel gruppo serve disattivare la privacy mode via BotFather.

È una decisione di prodotto, non tecnica: un bot che legge tutto il gruppo famigliare va dichiarato chiaramente nella privacy policy, e il consenso va reso esplicito. L'alternativa (richiedere sempre `/list latte` o la menzione) è più povera come esperienza ma molto più difendibile sul piano della privacy. **Raccomandazione: partire con privacy mode attiva** e valutare in base all'attrito reale.

### Comandi e inline keyboard

I comandi nativi registrati via `setMyCommands` compaiono nel menu del client e sono l'anti-LLM per eccellenza: `/list`, `/expense`, `/month`, `/link`, `/language`, `/help`. L'utente quotidiano impara le scorciatoie e quelle non passano mai dal modello.

**I nomi dei comandi sono canonici in inglese, non localizzati.** `setMyCommands` accetta un `language_code` e permetterebbe di mostrare `/lista` agli italiani, ma in un gruppo misto due membri vedrebbero menu diversi, e un comando copiato dall'uno all'altro non funzionerebbe. Le **descrizioni** invece sono localizzate, e gli alias italiani (`/lista`, `/spesa`) vengono accettati dal router senza comparire nel menu. Dettagli in [09-localizzazione.md](09-localizzazione.md).

Da decidere subito: rinominare i comandi dopo confonde chi li ha già imparati.

Le inline keyboard sono lo strumento migliore per le azioni ripetitive: dopo `/list` il bot mostra le voci con un bottone di spunta per ciascuna. Ogni tap è un `callback_query` gestito in modo deterministico, costo LLM zero.

## Linking bot ↔ console

Il flusso pulito su Telegram sfrutta il deep link con payload.

```
1. Utente autenticato sulla console clicca "Collega Telegram"
2. Backend genera LinkToken (32 byte random, TTL 10 min, monouso)
3. La pagina apre  https://t.me/tesseraapp_bot?start=<token>
4. Telegram invia al webhook  /start <token>
5. Backend: valida token (esiste, non scaduto, non consumato)
             crea ChannelIdentity(userId, "telegram", from.Id, chat.Id)
             marca il token consumato
6. Bot risponde "Collegato come <nome>"
```

Il token nel link è a tutti gli effetti una credenziale: se qualcuno lo intercetta, associa la propria chat all'account. Da qui TTL corto, monouso, e nessuna rigenerazione automatica.

**Su WhatsApp il deep link con payload non esiste.** `wa.me/<numero>?text=...` precompila il testo ma l'utente può modificarlo, quindi non è affidabile come canale di trasporto di un segreto. Il fallback è un **codice a 6 cifre** mostrato in console che l'utente invia al bot: TTL 10 minuti, massimo 5 tentativi, rate limit per numero. Meno elegante ma robusto.

## WhatsApp Cloud API — fase 3

Da affrontare **solo dopo** aver visto la retention su Telegram. Il costo non è di sviluppo, è di setup burocratico e di modello economico.

### Requisiti di ingresso

- Meta Business Account con **business verification** (documenti societari; come freelance con P.IVA è fattibile, ma i tempi variano)
- Numero di telefono dedicato, non associato ad account WhatsApp personale o Business App
- WhatsApp Business Account (WABA) collegato
- App su Meta for Developers con revisione dei permessi

### I due vincoli che cambiano il prodotto

**1. Nessun supporto ai gruppi.** La Cloud API è 1:1 fra business e singolo numero. La lista condivisa non può essere "il bot nel gruppo famiglia": si implementa come *N conversazioni separate legate allo stesso spazio*. L'utente A scrive al bot, il bot notifica B nella sua chat privata.

Funziona, ma è un'esperienza diversa e va progettata come tale. Il `ChannelCapabilities.SupportsGroups = false` esiste per questo.

**2. Finestra di 24 ore.** Dopo un messaggio dell'utente si può rispondere liberamente per 24 ore. Fuori da quella finestra si possono inviare solo **template pre-approvati**, a pagamento per conversazione.

Questo colpisce esattamente il caso d'uso proattivo:

| Notifica | Dentro 24h | Fuori 24h |
|---|---|---|
| "Sara ha aggiunto il pane" | Gratis | Template a pagamento |
| "Domani hai la riunione alle 9" | Gratis | Template a pagamento |

Un promemoria mattutino cade quasi sempre fuori finestra. Con qualche notifica al giorno per utente, il costo per utente diventa la voce dominante del progetto — vedi [04-costi.md](04-costi.md).

Mitigazioni progettuali: raggruppare le notifiche in un digest unico quotidiano invece di una per evento; rendere le notifiche proattive opt-in; su Telegram lasciarle libere e su WhatsApp limitarle. La differenza di canale va esposta all'utente, non nascosta.

### Validazione della firma

```csharp
// X-Hub-Signature-256: sha256=<hmac>
var computed = Convert.ToHexString(
    HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), rawBody)).ToLowerInvariant();
var valid = CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes($"sha256={computed}"),
    Encoding.UTF8.GetBytes(receivedHeader));
```

L'HMAC va calcolato sul **body raw**, prima di qualunque deserializzazione. Serve `EnableBuffering()` o un middleware che catturi il corpo grezzo.

## Login social (Google) — console

`AddGoogle` (ASP.NET Core Identity, `Program.cs`) usa **lo stesso client OAuth** di Google Calendar più sotto — stesso `Google:ClientId`/`Google:ClientSecret`, nessun secondo progetto da creare in Google Cloud Console. Sono due autorizzazioni indipendenti sullo stesso client:

| | Login | Google Calendar |
|---|---|---|
| Scope | `openid`, `profile`, `email` (**non sensitive**) | `calendar.*` (**sensitive**, review Google) |
| Serve un refresh token? | No — una lettura una tantum delle claim al sign-in | Sì, in Key Vault (regola 4) |
| Redirect URI | `/signin-google` (default del middleware) | `/oauth/google/callback` (`CalendarOAuthEndpoints`) |

Entrambi i redirect URI vanno registrati sullo stesso client, per ambiente (dev e produzione). Aggiungere `profile`/`email` come scope di login **non riapre la review Google**: sono scope non sensibili, concessi anche mentre l'app è in coda per la review dei permessi Calendar.

Nome e foto dell'account (`User.DisplayName`/`PictureUrl`) sono valorizzati dalle claim `name`/`picture` **solo alla creazione dell'account** (`ExternalLogin.razor`) — un utente esistente che collega Google in un secondo momento non li sovrascrive, e un valore impostato a mano da Profilo resta tale sui sign-in successivi.

## Google Calendar — fase 2

### Classificazione degli scope

| Scope | Classe | Implicazione |
|---|---|---|
| `calendar.readonly` | Sensitive | OAuth verification standard |
| `calendar.events` | Sensitive | OAuth verification standard |
| `calendar.freebusy` | Sensitive (leggero) | Il minimo per le disponibilità |
| `gmail.readonly` e simili | **Restricted** | Security assessment CASA annuale |

La differenza è sostanziale. **Sensitive** significa una review manuale con documentazione: settimane di attesa, nessun costo ricorrente. **Restricted** significa un audit di sicurezza da terza parte accreditata, con costo annuo nell'ordine delle migliaia di euro. Per questo Gmail è in Fase 4 e potrebbe non arrivare mai.

### Cosa serve per la verification

- Homepage pubblica sul dominio verificato (Search Console) — la console web assolve
- Privacy policy raggiungibile e specifica sull'uso dei dati Google
- Video demo su YouTube che mostri il consent screen e l'uso concreto di ogni scope
- Giustificazione scritta per ciascuno scope richiesto

Tempi realistici: **2-6 settimane**, con giri di richieste di chiarimenti. Da avviare all'inizio della Fase 1, non alla fine: matura mentre si sviluppa altro.

### Il vincolo dello stato "Testing"

Finché l'app OAuth è in *Testing*: massimo 100 utenti test, e **i refresh token scadono dopo 7 giorni**. Per prototipare va bene. Per un assistente reale è inutilizzabile: chiedere il re-login ogni settimana uccide il prodotto.

Conseguenza pratica: pianificare la verification come dipendenza della Fase 2, non come adempimento successivo.

### Enumerare i calendari, non assumerne uno

Un account espone molti calendari, e la scelta di quali usare è dell'utente. Dopo il collegamento OAuth il primo passo è popolare `ExternalCalendar` (vedi [02-modello-dati.md](02-modello-dati.md)).

```
GET https://www.googleapis.com/calendar/v3/users/me/calendarList
```

Campi che contano nella risposta:

| Campo | Uso |
|---|---|
| `id` | → `ProviderCalendarId` |
| `summary` | → `Name` (per i calendari condivisi può essere `summaryOverride`) |
| `primary` | → `IsPrimary`, l'unico da abilitare per default |
| `accessRole` | → `ProviderRole`: `freeBusyReader`, `reader`, `writer`, `owner` |
| `selected` | Suggerimento di quali l'utente usa davvero nell'interfaccia Google |
| `backgroundColor` | → `Color`, utile per riprodurre i colori familiari in console |

`accessRole` è il vincolo esterno: su un calendario `reader` la creazione di eventi fallisce con 403 indipendentemente dai permessi interni. Va conservato e rispettato prima di proporre l'azione, non scoperto al momento della chiamata.

**Abilitare solo il primario per default.** La `calendarList` contiene tipicamente "Compleanni", "Festività in Italia" e calendari dimenticati: abilitarli tutti produce una vista unita illeggibile proprio nel momento in cui l'utente giudica la funzione. La console mostra l'elenco completo con toggle.

La lista va **rinfrescata periodicamente**: i calendari condivisi possono essere aggiunti, rimossi o cambiare `accessRole` lato provider. Un refresh al collegamento e poi quotidiano è sufficiente; un calendario sparito va marcato come non disponibile, non cancellato, per non perdere le mappature agli spazi.

### Vista unita e disponibilità su più calendari

`freebusy.query` accetta più calendari in una sola chiamata, il che rende il calcolo delle disponibilità efficiente anche con molti calendari mappati:

```
POST https://www.googleapis.com/calendar/v3/freeBusy
{
  "timeMin": "2026-08-06T00:00:00+02:00",
  "timeMax": "2026-08-07T00:00:00+02:00",
  "items": [
    { "id": "primary" },
    { "id": "famiglia123@group.calendar.google.com" }
  ]
}
```

Per i calendari mappati con `CalendarShareLevel.Availability` si usa **solo** questo endpoint, mai la lettura degli eventi: garantisce a livello di API che i titoli non possano finire nella risposta. È una misura di minimizzazione difendibile anche in fase di OAuth review, oltre che corretta.

Per quelli con `Details` si legge `events.list`, e i risultati dei due tipi si fondono in un'unica vista dove alcune fasce hanno il titolo e altre solo l'orario.

### Deduplica: il calendario condiviso collegato da entrambi

Lo scenario è normale, non limite: due coniugi hanno accesso allo stesso calendario "Famiglia", entrambi collegano il proprio account, entrambi lo abilitano. Nello spazio "Casa" quel calendario risulta due volte e ogni evento appare duplicato — nel calcolo delle disponibilità, ogni fascia occupata viene contata due volte.

La deduplica si fa su `iCalUID` (Google) o `iCalUId` (Microsoft — nota la maiuscola diversa fra i due provider, è una fonte di bug), che restano stabili fra le copie dello stesso evento. Dettaglio in [02-modello-dati.md](02-modello-dati.md).

Meglio ancora: prevenirlo in console segnalando che quel calendario è già collegato da un altro membro dello spazio.

### freebusy.query — la scelta giusta per la privacy

Per "quando siamo liberi io e Sara giovedì?" non serve leggere gli eventi. `freebusy.query` restituisce solo le fasce occupate, senza titoli né partecipanti.

```
POST https://www.googleapis.com/calendar/v3/freeBusy
{
  "timeMin": "2026-08-06T00:00:00+02:00",
  "timeMax": "2026-08-07T00:00:00+02:00",
  "items": [{ "id": "primary" }]
}
```

Questo mappa esattamente su `AccessLevel.Availability` del modello permessi: si può calcolare l'intersezione delle disponibilità di due membri senza che nessuno veda i dettagli dell'altro. È un buon argomento anche in fase di OAuth review — chiedere lo scope minimo aiuta l'approvazione.

**Ogni utente collega il proprio account.** Non serve (e non si deve) condividere l'OAuth: il backend legge entrambi i calendari con le rispettive autorizzazioni e compone la vista unita.

### Timezone

Gli eventi vanno gestiti con l'IANA timezone dell'utente, non con offset fissi. `DateTimeOffset` in memoria, `TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")` per le conversioni, e mai assumere il fuso del server. È la fonte più comune di bug in un assistente calendario, e si manifesta puntualmente al cambio d'ora.

## Microsoft Graph — fase 2

Molto più semplice di Google.

- Registrazione app su Entra ID
- **Publisher verification** (associazione a un Partner Center account): giorni, non settimane
- Scope: `Calendars.Read`, `Calendars.ReadWrite`, `Calendars.Read.Shared`
- Per multi-tenant, consenso dell'amministratore solo su tenant aziendali; per account personali Microsoft il consenso è dell'utente
- SDK `Microsoft.Graph` maturo, `Microsoft.Identity.Web` gestisce token cache e refresh

Graph ha `getSchedule` come equivalente di `freebusy.query`, con la stessa proprietà di non esporre i dettagli degli eventi.

**Calendari multipli su Graph**, con qualche differenza da Google:

- `GET /me/calendars` enumera i calendari; `GET /me/calendarGroups` i gruppi, se servono
- Il permesso per calendario sta in `canEdit` e `canShare`, non in un unico `accessRole` come Google — la mappatura verso `ProviderAccessRole` va scritta a mano
- I calendari **condivisi da altri** sono il punto meno lineare: possono comparire in `/me/calendars` oppure richiedere `Calendars.Read.Shared` e l'accesso via `/users/{id}/calendars`. Il comportamento varia fra account personali Microsoft e tenant aziendali, quindi va verificato empiricamente su entrambi i tipi di account prima di considerare la funzione completa
- `iCalUId` con la I maiuscola, contro `iCalUID` di Google: la deduplica cross-provider deve normalizzare

L'ultima riga del punto 3 è la ragione per cui conviene implementare Microsoft prima di Google nonostante Google sia il caso d'uso più comune: le sorprese emergono presto e su un ciclo di feedback corto.

Suggerimento: **implementare Microsoft prima di Google.** Il ciclo di feedback è molto più corto e permette di validare l'astrazione del calendario mentre la review Google è in coda.

## PayPal Subscriptions — pagamenti

Decisione presa e **implementata**: **PayPal Subscriptions REST API**, non Stripe — motivazione fiscale (regime forfettario), non tecnica, in [04-costi.md](04-costi.md#provider-di-pagamento-paypal-non-stripe). Schema dati in [02-modello-dati.md](02-modello-dati.md#abbonamento-paypal-per-spazio). `PayPalClient` (`Tessera.Integrations`) implementa `IPaymentProvider` (`Tessera.Core.Abstractions`) — stessa ragione di `ICalendarProvider`: `Tessera.Data` (`PayPalSubscriptionService`) non deve referenziare `Tessera.Integrations` direttamente, anche se qui c'è una sola implementazione per scelta di prodotto, non per polimorfismo. Config opzionale — vedi [08-setup-sviluppo.md](08-setup-sviluppo.md); senza `PayPal:ClientId`/`ClientSecret`/`WebhookId` la feature resta silenziosamente disattivata, stesso pattern di Calendar/BlobStorage.

### Autenticazione: client_credentials, non authorization code

A differenza di Google/Microsoft Calendar, qui non c'è un account utente da collegare: Tessera stesso è il merchant. Un solo scambio OAuth2 `client_credentials` (Client ID/Secret dell'app PayPal, Basic auth su `POST /v1/oauth2/token`) restituisce un access token applicativo, usato per tutte le chiamate — creare/modificare piani, creare/leggere abbonamenti. Nessun refresh token da conservare in Key Vault: il token si rigenera on demand, ha vita breve (poche ore), non è per-utente.

### Prodotto e piani: creati una volta, non per spazio

`POST /v1/catalogs/products` (un prodotto, "Tessera") poi `POST /v1/billing/plans` (uno per ciascuno dei 4 piani a pagamento — Free non ha un piano PayPal). Fatto automaticamente all'avvio (`PayPalSubscriptionService.EnsurePlansProvisionedAsync`, idempotente), non serve setup manuale. L'id restituito da ciascuna chiamata va in `SubscriptionPlan.PayPalPlanIdSandbox` o `...IdLive` a seconda di `PayPal:Environment` — vedi [02-modello-dati.md](02-modello-dati.md#abbonamento-paypal-per-spazio) per il perché delle due colonne separate. Il prodotto stesso non ha questo problema: sandbox e live sono account PayPal completamente separati, quindi `EnsureProductAsync` (che cerca per nome invece di salvare l'id) non rischia mai di trovare il prodotto dell'altro ambiente.

### Flusso di sottoscrizione

1. Console: l'utente sceglie un piano → `POST /v1/billing/subscriptions` con il `PayPalPlanId` corrispondente → la risposta contiene un link HATEOAS `rel: "approve"`.
2. Redirect dell'utente su quel link (dominio PayPal) per l'approvazione del pagamento.
3. PayPal reindirizza a un `return_url`/`cancel_url` nostro con l'id della subscription in query string — **da trattare solo come segnale UX**, non come conferma di attivazione: un utente può chiudere il browser dopo aver approvato ma prima del redirect.
4. Il webhook è l'unica fonte di verità sullo stato reale (stesso principio del hard rule 6 sui webhook Telegram: 200 OK subito, elaborazione in background).

### Webhook e validazione della firma

Endpoint dedicato (`/hooks/paypal`, `PayPalWebhookEndpoints`), stesso pattern di deduplica di Telegram — riusa la stessa tabella `ProcessedMessage` (`ChannelName = "paypal"`, `ProviderMessageId = event_id`) invece di una tabella dedicata, perché la forma del problema è identica. La firma va verificata via `POST /v1/notifications/verify-webhook-signature`, passando gli header `PAYPAL-TRANSMISSION-ID`, `PAYPAL-TRANSMISSION-TIME`, `PAYPAL-CERT-URL`, `PAYPAL-AUTH-ALGO`, `PAYPAL-TRANSMISSION-SIG` più il `webhook_id` configurato lato app — **mai fidarsi di un evento senza questa verifica**, a differenza dell'HMAC calcolabile localmente di WhatsApp, qui la verifica richiede una chiamata di rete a PayPal per ogni evento ricevuto, fatta **prima** di dedurre l'evento come processato (una firma non valida risponde `401`, non `200`).

Eventi da gestire per aggiornare `SpaceSubscription`/`Space.PlanId`:

| Evento | Effetto |
|---|---|
| `BILLING.SUBSCRIPTION.ACTIVATED` | `Space.PlanId` → piano acquistato, `SpaceSubscription.Status` → `ACTIVE` |
| `BILLING.SUBSCRIPTION.SUSPENDED` | Pagamento fallito → `Space.PlanId` → `Free` immediatamente, nessun periodo di grazia (deciso, [02-modello-dati.md](02-modello-dati.md#abbonamento-paypal-per-spazio)) |
| `BILLING.SUBSCRIPTION.CANCELLED` / `EXPIRED` | `Space.PlanId` → `Free` |
| `BILLING.SUBSCRIPTION.UPDATED` | Cambio piano confermato (vedi sotto) → `Space.PlanId` ri-sincronizzato con `SpaceSubscription.PlanId` |
| `PAYMENT.SALE.COMPLETED` | Rinnovo periodico riuscito — utile per un log dei pagamenti, non cambia lo stato del piano |

### Cambio piano fra due abbonamenti a pagamento

`POST /v1/billing/subscriptions/{id}/revise` cambia il `plan_id` di un abbonamento `ACTIVE` già esistente, invece di crearne uno nuovo — stesso `PayPalSubscriptionId`, cambia solo cosa fattura. `PayPalSubscriptionService.ReviseSubscriptionAsync` aggiorna subito `SpaceSubscription.PlanId`; se la risposta contiene un link `rel: "approve"` l'utente va rimandato lì per confermare (stesso principio del flusso di sottoscrizione — solo dopo webhook `BILLING.SUBSCRIPTION.UPDATED` si aggiorna anche `Space.PlanId`), altrimenti significa che PayPal ha applicato il cambio subito, e `Space.PlanId` viene aggiornato immediatamente senza aspettare webhook.

Testato in sandbox — entrambi i casi (con e senza riapprovazione richiesta da PayPal) sono gestiti dal codice.

### Cancellazione dalla console

`POST /v1/billing/subscriptions/{id}/cancel` — a differenza di sottoscrizione e cambio piano non ha un passaggio di conferma lato PayPal: risponde `204` e basta. `PayPalSubscriptionService.CancelSubscriptionAsync` applica `Space.PlanId → Free` immediatamente, senza aspettare il webhook `BILLING.SUBSCRIPTION.CANCELLED` che arriva comunque poco dopo a riconferma (gestito in modo idempotente da `HandleWebhookEventAsync`, quindi non fa danno se applica di nuovo lo stesso stato). Disponibile per abbonamenti `ACTIVE` o `APPROVAL_PENDING` — nel secondo caso l'utente abbandona un'approvazione mai completata.

### Sandbox

App separata su [developer.paypal.com](https://developer.paypal.com) con le proprie credenziali (`api-m.sandbox.paypal.com` invece di `api-m.paypal.com`) e conti sandbox buyer/merchant finti — permette di testare l'intero ciclo (sottoscrizione, rinnovo, cancellazione, webhook) senza soldi reali. Da configurare come coppia di variabili separate (`PayPal:Environment` = `sandbox`/`live`), stesso principio del profilo `http`/`https` già in `launchSettings.json`.

## Azure Communication Services — email di reset password

Prima integrazione email dell'app: fino ad ora non esisteva alcuna infrastruttura di invio (`Program.cs` lo documentava esplicitamente come rimandato). Scope volutamente stretto — **solo il recupero password** (`/Account/ForgotPassword`, `/Account/ResetPassword`); `RequireConfirmedAccount` resta `false`, nessun'altra email transazionale (inviti, digest restano solo Telegram).

### Perché ACS e non SendGrid/SMTP

Resta nello stack Azure già in uso (App Service, Key Vault, Azure SQL) invece di aggiungere un account terzo. Autenticazione via la stessa Managed Identity già concessa per Key Vault (`DefaultAzureCredential`, scope `https://communication.azure.com/.default`) — nessun nuovo segreto da custodire, a differenza di una API key SendGrid che andrebbe comunque in Key Vault/user-secrets.

### Client scritto a mano, non l'SDK ufficiale

`AzureEmailClient` (`Tessera.Integrations`) chiama direttamente `POST {endpoint}/emails:send` invece di referenziare `Azure.Communication.Email` — stesso principio già seguito per PayPal e per i calendari: un client sottile scritto in casa invece di un pacchetto SDK per una singola chiamata REST. A differenza del flusso `client_credentials` di PayPal, qui non serve nemmeno una cache manuale del token: `TokenCredential` di Azure.Identity la gestisce già internamente.

### Contenuto dell'email nella lingua del destinatario

Prima di comporre oggetto e corpo, `ForgotPassword.razor` imposta `CultureInfo.CurrentCulture`/`CurrentUICulture` dalla `PreferredCulture` salvata sull'utente — stesso identico idioma già usato da `NotificationService` per le notifiche di spazio condiviso (hard rule 8/9-localizzazione.md), non la cultura ambientale della richiesta HTTP di chi ha compilato il form (che potrebbe non essere nemmeno loggato).

### Anti-enumerazione

`ForgotPassword` e `ResetPassword` mostrano sempre lo stesso esito (pagina di conferma generica) sia che l'email corrisponda a un account sia che non esista — altrimenti il form diventerebbe un modo per scoprire quali indirizzi sono registrati.

### Configurazione

`Email:Endpoint` + `Email:SenderAddress` — se assenti, il servizio semplicemente non viene registrato (stesso pattern di ogni altra integrazione opzionale in `Program.cs`): la pagina `/Account/ForgotPassword` resta visibile ma non invia nulla. Passi di provisioning manuale (risorsa ACS, dominio, ruolo sulla Managed Identity) in `docs/08-setup-sviluppo.md`.

## Alexa — non praticabile, decisione presa

L'obiettivo iniziale era il **sync bidirezionale** fra la lista della spesa del bot e la lista nativa di Alexa. Non è realizzabile.

### Perché

Il sync richiedeva due cose:

1. **Household List API** — accesso in lettura/scrittura alle liste dell'utente
2. **Eventi push** (`ItemsCreated`, `ItemsUpdated`, `ItemsDeleted`) inviati alla skill quando l'utente modifica la lista nativa

Amazon ha deprecato l'accesso pubblico a queste API nel corso del 2024 — la stessa chiusura che ha rotto le integrazioni di AnyList, Bring! e Todoist. L'accesso è rimasto a pochi partner in accordo diretto, non disponibile a uno sviluppatore singolo tramite developer portal.

Gli eventi push erano l'**unico** canale per sapere che qualcosa era cambiato lato Alexa: non esiste polling, non esiste export. Senza di essi il sync in quella direzione è strutturalmente impossibile, non solo scomodo.

### Alternative valutate e scartate

| Opzione | Perché no |
|---|---|
| Endpoint interni `alexa.amazon.com` (approccio `alexapy`) | Richiede credenziali o cookie di sessione Amazon dell'utente, viola i ToS, si rompe a ogni cambio lato Amazon. Inaccettabile in un prodotto che chiede già OAuth su Google e Microsoft: la fiducia costruita lì si brucia qui. |
| IFTTT / Home Tessera come ponte | Dipendono dalle stesse API a monte. Sposta il problema e aggiunge un terzo sistema da configurare per l'utente. |
| Custom skill con invocation name | Praticabile, ma non è sync: è una lista separata. Richiede account linking OAuth e certificazione Amazon per un guadagno marginale rispetto a scrivere sul bot. |

### Nota di merito

Anche se l'API tornasse disponibile, il sync bidirezionale di liste è intrinsecamente problematico: due sorgenti che si scrivono a vicenda, nessun timestamp affidabile, cancellazioni senza eventi puliti, e il classico loop in cui il sistema A scrive su B, B notifica, A riscrive. Servirebbe un vector clock o un last-write-wins con tombstone — complessità sproporzionata per una lista della spesa.

### Sostituto per il caso d'uso "voce"

La dettatura vocale nativa di Telegram e WhatsApp sul telefono copre gran parte del bisogno a costo zero. Se in futuro emerge che l'Echo in cucina è il punto d'ingresso principale dell'utenza, la custom skill torna in roadmap come Fase 5 — mai come MVP.
