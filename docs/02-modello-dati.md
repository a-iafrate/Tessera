# 02 — Modello dati

## La decisione irreversibile

Il modello **non** è `Utente → Lista`. È:

```
Utente → Membership(ruolo per tipo di risorsa) → Spazio → Risorse
```

Questa è l'unica scelta del progetto che non si rifattorizza a costo basso. Partire da "ogni utente ha la sua lista" e aggiungere la condivisione dopo significa riscrivere ogni query, ogni controllo di autorizzazione e migrare i dati esistenti. La condivisione è il cuore del prodotto: va nello schema dal primo giorno, anche se in Fase 1 tutti gli spazi hanno un solo membro.

## Entità

### Utente e identità di canale

```csharp
public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;         // account console
    public string? DisplayName { get; set; }
    public string? PictureUrl { get; set; }               // URL, non un blob — vedi 03/07
    public string PreferredCulture { get; set; } = "en";  // IETF: "it", "en" — vedi 09
    public string? TimeZoneId { get; set; }               // IANA: "Europe/Rome"
    public Guid? DefaultSpaceId { get; set; }             // disambiguazione in chat privata
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ChannelIdentity> ChannelIdentities { get; set; } = [];
    public ICollection<LinkedAccount> LinkedAccounts { get; set; } = [];
    public ICollection<Membership> Memberships { get; set; } = [];
}

public class ChannelIdentity        // "questo chat_id Telegram è questo utente"
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ChannelName { get; set; } = null!;   // "telegram" | "whatsapp"
    public string ExternalUserId { get; set; } = null!;
    public string? ExternalChatId { get; set; }        // chat privata con il bot
    public DateTimeOffset LinkedAt { get; set; }
}

public class LinkedAccount          // "questo utente ha collegato Google"
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ProviderKind Provider { get; set; }         // Google | Microsoft
    public string ProviderUserId { get; set; } = null!;
    public string? ProviderEmail { get; set; }
    public string TokenSecretName { get; set; } = null!; // riferimento a Key Vault, non il token
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}
```

**Tre concetti distinti che è facile confondere:**

1. **Account console** (`ApplicationUser`, Identity — vedi sotto) — chi si logga sul web
2. **Identità di canale** (`ChannelIdentity`) — quale chat_id corrisponde a quell'utente
3. **Account collegato** (`LinkedAccount`) — quali servizi esterni ha autorizzato

Un utente può loggarsi sulla console con Google e *non* aver autorizzato Google Calendar. Sono flussi OAuth diversi con scope diversi. Tenerli separati nello schema evita un rimaneggiamento certo.

### Account console: `ApplicationUser` vs `User`

Login con email/password fin dal giorno uno, provider social (Google, Microsoft) aggiunti in fase successiva — decisione e motivazione in [01-architettura.md](01-architettura.md). Meccanismo: **ASP.NET Core Identity**, non un campo password su `User`.

Questo introduce una seconda entità, tenuta deliberatamente separata dal dominio:

```csharp
// Tessera.Data — infrastruttura, gestita da ASP.NET Core Identity
public class ApplicationUser : IdentityUser<Guid>
{
    // PasswordHash, SecurityStamp, ecc. ereditati da IdentityUser<Guid>
    // I login social futuri vivono in AspNetUserLogins, nessuna modifica a questa classe
}
```

`ApplicationUser` e `User` (dominio, sopra) condividono lo stesso `Guid Id`, creati insieme in un'unica operazione alla registrazione — insieme allo spazio "Personale", per la regola di "condivisibile per costruzione" descritta più sotto. `Tessera.Core` non referenzia mai `ApplicationUser` né alcun tipo di Identity: la logica di dominio conosce solo `User`. La risoluzione fra i due (in login, in ogni query che parte da `HttpContext.User`) è responsabilità di `Tessera.Web`, mai di `Tessera.Core`.

Nota su `LinkedAccount`: nel database sta solo il **nome del segreto**, mai il refresh token. Vedi [07-compliance.md](07-compliance.md).

Nota su `PreferredCulture` e `TimeZoneId`: stanno su `User`, non su `ChannelIdentity`. Un utente che usa sia Telegram che WhatsApp ha una sola lingua preferita, impostabile da console o da `/language` e valida su tutti i canali. Il `LanguageCode` che Telegram fornisce nel messaggio è solo il default alla prima interazione. Vedi [09-localizzazione.md](09-localizzazione.md).

### Spazio

```csharp
public class Space
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;          // "Casa", "Amici tennis"
    public Guid OwnerId { get; set; }
    public string Currency { get; set; } = "EUR";      // valuta dello spazio, vedi 09
    public DateTimeOffset CreatedAt { get; set; }

    public Guid PlanId { get; set; }                   // mai null, vedi "Piano di abbonamento"
    public ICollection<Membership> Memberships { get; set; } = [];
    public string? GroupChatId { get; set; }           // se il bot è in un gruppo Telegram
    public string? GroupChannelName { get; set; }
}
```

Ogni utente ha almeno uno spazio personale creato alla registrazione. Uno spazio può essere legato a un gruppo Telegram: in quel caso i messaggi nel gruppo agiscono su quello spazio senza disambiguazione.

`Space.Currency` sta sullo spazio e non sull'utente: una spesa condivisa è in una valuta sola, mentre il *formato* con cui viene mostrata dipende dalla cultura di chi legge. Confondere le due cose è un classico — vedi [09-localizzazione.md](09-localizzazione.md).

### Piano di abbonamento

```csharp
public class SubscriptionPlan
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;          // "Free", "Basic", "Plus", "Family"
    public int MaxLinkedBots { get; set; }             // canali/identità collegabili allo spazio
    public int MaxCallsPerDay { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string Currency { get; set; } = "EUR";
}
```

Il piano è **per spazio**, non per utente: una famiglia con più membri in uno spazio condiviso paga un piano unico, non uno a testa. `Space.PlanId` non è mai null — ogni spazio nasce sul piano `Free` (`SystemPlanIds.Free`), assegnato da `UserProvisioningService` alla creazione, esattamente come le categorie di sistema in "Categorie di spesa" più sotto.

I piani sono righe seedate (`SubscriptionPlanConfiguration.HasData`), condivise fra tutti gli spazi che le referenziano — non una copia per spazio. Valori attuali (placeholder, modificabili senza toccare lo schema):

| Piano | Bot collegabili | Chiamate/giorno | Prezzo/mese |
|---|---|---|---|
| Free | 1 | 20 | 0 € |
| Basic | 1 | 200 | 5 € |
| Plus | 3 | 1000 | 12 € |
| Family | 10 | 5000 | 25 € |

**Enforcement**: `MaxCallsPerDay` (`UsageService.TryRecordL3CallAsync`) e `AllowsReceiptScanning` (`MessageProcessor`) sono applicati. `MaxLinkedBots` è applicato solo al collegamento di un **gruppo Telegram** a uno spazio (`LinkService.CanLinkAnotherBotAsync`, controllato nei due punti di `MessageProcessor` dove `Space.GroupChatId` viene impostato) — non al collegamento dell'account Telegram privato di un singolo membro, perché quell'azione non è scopabile a un singolo spazio: `ChannelIdentity` è per-utente, non per-spazio, e un utente può appartenere a più spazi contemporaneamente. Il conteggio (`LinkService.GetLinkedBotCountAsync`) è quindi derivato: membri dello spazio con almeno un'identità collegata (esclusa la chat web, che non conta) più uno se lo spazio ha un gruppo Telegram collegato. Il rate limiting fisso da 60 messaggi/ora per identità (vedi [07-compliance.md](07-compliance.md)) resta comunque attivo in parallelo, indipendente dal piano.

### Abbonamento PayPal per spazio

**PayPal Subscriptions REST API** come flusso di pagamento reale (non Stripe — vedi [04-costi.md](04-costi.md#piani-a-pagamento) per il perché). Implementato.

```csharp
public class SpaceSubscription
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public string PayPalSubscriptionId { get; set; } = null!;  // "I-XXXXXXXXXXXX", assegnato da PayPal alla creazione
    public Guid PlanId { get; set; }                            // FK a SubscriptionPlan
    public string Status { get; set; } = null!;                 // rispecchia lo stato PayPal: APPROVAL_PENDING, ACTIVE, SUSPENDED, CANCELLED, EXPIRED
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

Entità separata da `Space`, stesso principio di `LinkedAccount`/`ExternalCalendar`: lo stato di un abbonamento cambia per eventi esterni (webhook PayPal), non ha senso annidarlo in `Space` come colonne dirette. `SubscriptionPlan` guadagna due campi:

```csharp
public string? PayPalPlanIdSandbox { get; set; }  // id del billing plan lato PayPal (v1/billing/plans)
public string? PayPalPlanIdLive { get; set; }      // null per Free, che non ha un piano PayPal
```

Due colonne, non una: il database è condiviso fra test e produzione (docs/03-integrazioni.md), e sandbox/live sono account PayPal diversi con id incompatibili fra loro. Una sola colonna avrebbe fatto sì che il primo avvio in `live` trovasse la colonna già valorizzata dal sandbox e non creasse i piani veri — bug scoperto e corretto prima di andare in produzione, non dopo.

`Space.PlanId` resta la fonte di verità per l'enforcement (`MaxLinkedBots`/`MaxCallsPerDay`) — `SpaceSubscription` è lo stato del pagamento che lo tiene aggiornato, aggiornato dagli eventi webhook (`BILLING.SUBSCRIPTION.ACTIVATED` → aggiorna `Space.PlanId` al piano acquistato, `BILLING.SUBSCRIPTION.CANCELLED`/`EXPIRED` → retrocede a `Free`). Dettagli del flusso OAuth/webhook in [03-integrazioni.md](03-integrazioni.md#paypal-subscriptions--pagamenti).

**Deciso**: su `BILLING.SUBSCRIPTION.SUSPENDED`, retrocessione immediata a `Free` — nessun periodo di grazia. Il downgrade non perde dati (liste, spese, promemoria restano intatti), limita solo le funzioni oltre le soglie del piano gratuito, quindi non serve ammortizzare l'effetto con un avviso preventivo.

### Un bot, molte chat, spazi indipendenti

Lo stesso bot vive contemporaneamente in più chat con comportamenti autonomi. L'isolamento **non lo fa Telegram**: ogni update arriva allo stesso webhook, e la separazione la produce la risoluzione `chat_id → spazio` nel codice.

| Chat | Risoluzione | Spazio |
|---|---|---|
| Gruppo "Casa" | `Space.GroupChatId` → match diretto | Casa |
| Gruppo "Amici tennis" | `Space.GroupChatId` → match diretto | Amici tennis |
| Chat privata dell'utente A | `ChannelIdentity` + catena di precedenza | Personale o `DefaultSpaceId` |
| Chat privata dell'utente B | La sua `ChannelIdentity` | Il suo Personale |

Nel gruppo l'ambiguità non esiste. In chat privata serve la catena descritta più sotto, perché l'utente appartiene a più spazi.

**Il permesso resta per persona anche nel gruppo.** Ogni messaggio porta `from.Id`, quindi `AddedByUserId` funziona e `IAccessPolicy` si applica al singolo membro: due persone nello stesso gruppo possono avere capacità diverse sulla stessa risorsa. L'isolamento è per **(persona × spazio × risorsa)**, non per chat.

### Migrazione del chat_id di gruppo

Quando un gruppo Telegram viene convertito in **supergruppo** — cosa che avviene automaticamente aggiungendo membri, cambiando permessi o abilitando lo storico — **il `chat_id` cambia**. Il vecchio id diventa inutilizzabile.

Se non si gestisce, `Space.GroupChatId` punta a un gruppo che non esiste più e il bot **smette silenziosamente di riconoscere lo spazio**: nessun errore, semplicemente non risponde più in quel gruppo. È un bug che compare mesi dopo il setup, quando nessuno collega più la causa all'effetto.

```csharp
public class Space
{
    // ...
    public string? GroupChatId { get; set; }
    public string? PreviousGroupChatId { get; set; }   // traccia della migrazione
}
```

Telegram segnala l'evento in due modi, ed entrambi vanno gestiti — vedi [03-integrazioni.md](03-integrazioni.md) per il codice.

`PreviousGroupChatId` non serve al funzionamento ma alla diagnosi: se qualcosa va storto dopo una migrazione, sapere quale era il vecchio id fa la differenza fra dieci minuti e un pomeriggio.

### Condivisibile per costruzione

La risorsa personale è il **caso degenere**, non il caso base: uno spazio con un solo membro. Questo ha tre conseguenze da rispettare nel codice, non solo nello schema.

**1. Nessuna risorsa esiste senza `SpaceId`.** Niente nullable, niente branch "se è personale allora...". Alla registrazione si crea uno spazio "Personale" con l'utente come owner e unico membro. Ogni risorsa nasce dentro uno spazio, sempre.

**2. Nessuna query per `UserId` su una risorsa.** Il filtro è sempre `SpaceId IN (spazi accessibili all'utente)`. Trovarsi a scrivere `WHERE UserId = @me` su una lista o una spesa significa aver già introdotto il bug che questo modello serve a evitare. Un query filter globale EF, o repository che richiedono obbligatoriamente lo `SpaceId` risolto a monte, rendono l'errore difficile da commettere.

**3. Il "chi ha fatto cosa" non è opzionale.** `AddedByUserId`, `CheckedByUserId`, `CreatedByUserId` su ogni risorsa. In un modello personale sarebbero campi inutili; in uno condiviso sono la base delle notifiche e delle domande reali ("chi ha aggiunto le olive?").

### Disambiguazione dello spazio in chat privata

Nel gruppo Telegram il problema non esiste: `GroupChatId` → spazio. In chat privata invece, se l'utente appartiene a tre spazi, "aggiungi il latte" va deciso.

Schema adottato, in ordine di precedenza:

```
1. Spazio esplicito nel messaggio       "aggiungi latte in Casa"
2. ConversationState.ActiveSpaceId      entro il TTL, se già disambiguato di recente
3. User.DefaultSpaceId                  impostato in console
4. Unico spazio con permesso Write      se ce n'è uno solo per quella risorsa
5. Domanda con inline keyboard          e memorizzazione in ConversationState
```

Il punto 4 è quello che risolve silenziosamente la maggior parte dei casi: se l'utente ha permesso di scrittura sulla lista della spesa in un solo spazio, non c'è ambiguità da risolvere anche se appartiene a cinque spazi. La disambiguazione è **per risorsa**, non per utente.

Il punto 5 va reso raro: una domanda al giorno è accettabile, una a ogni messaggio è un prodotto inutilizzabile. Per questo il punto 2 memorizza la scelta per la durata della sessione conversazionale, e il punto 3 è impostabile in console.

`DefaultSpaceId` è su `User` e non su `ChannelIdentity`: la preferenza è della persona, non del canale. Se in futuro emergesse il bisogno di default per canale (WhatsApp per casa, Telegram per il lavoro), si sposta — ma è una complicazione da non anticipare.

### Membership e permessi granulari

Il punto delicato. Un ruolo unico per spazio non basta: "lista della spesa con la moglie" e "sola disponibilità del calendario con gli amici" sono livelli diversi nello stesso modello. Il permesso è **per tipo di risorsa**.

```csharp
public enum ResourceKind
{
    ShoppingList = 1,
    Expenses     = 2,
    Reminders    = 3,
    Calendar     = 4,
    Documents    = 5,   // fase 4
    Email        = 6,   // fase 4
    Notes        = 7
}

public enum AccessLevel
{
    None      = 0,
    Availability = 1,   // solo per Calendar: free/busy, nessun dettaglio evento
    Read      = 2,
    Write     = 3,
    Admin     = 4       // può invitare, rimuovere membri, cambiare permessi
}

public class Membership
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public bool IsOwner { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public ICollection<MembershipPermission> Permissions { get; set; } = [];
}

public class MembershipPermission
{
    public Guid MembershipId { get; set; }
    public ResourceKind Resource { get; set; }
    public AccessLevel Level { get; set; }
}
```

`AccessLevel.Availability` esiste solo per `Calendar` e vale la sua eccezione: è la differenza fra "vedo che giovedì sei occupato dalle 14 alle 15" e "vedo che giovedì hai il colloquio con l'avvocato". È esattamente il caso d'uso "trova quando siamo liberi" e va implementato con `freebusy.query`, che restituisce solo le fasce senza titoli. Vedi [03-integrazioni.md](03-integrazioni.md).

Preset consigliati in console, per non esporre una matrice di checkbox all'utente:

| Preset | ShoppingList | Expenses | Reminders | Calendar |
|---|---|---|---|---|
| Partner | Write | Write | Write | Read |
| Familiare | Write | None | Write | Availability |
| Amico | None | None | None | Availability |
| Personalizzato | — | — | — | — |

### Risorse: liste e spese

```csharp
public class ShoppingList
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public string Name { get; set; } = "Spesa";
    public bool IsArchived { get; set; }
    public ICollection<ShoppingItem> Items { get; set; } = [];
}

public class ShoppingItem
{
    public Guid Id { get; set; }
    public Guid ShoppingListId { get; set; }
    public string RawText { get; set; } = null!;      // "2 litri di latte" — come detto dall'utente
    public string NormalizedName { get; set; } = null!; // "latte" — per lo storico prezzi, fase 4
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public bool IsChecked { get; set; }
    public Guid AddedByUserId { get; set; }
    public Guid? CheckedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
}
```

`RawText` e `NormalizedName` separati: il primo è quello che si mostra all'utente, il secondo serve all'aggregazione. La normalizzazione seria ("Latte Granarolo 1L" e "latte parzialmente scremato" sulla stessa entità) è lavoro sporco da Fase 4 — per ora basta lowercase, trim e rimozione degli articoli.

Tracciare `AddedByUserId` non è un dettaglio: su una lista condivisa "chi ha aggiunto le olive?" è una domanda reale, e serve per le notifiche agli altri membri.

```csharp
public class Expense
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public decimal Amount { get; set; }               // decimal(18,2), mai double
    public string Currency { get; set; } = "EUR";     // ereditata da Space.Currency alla creazione
    public string? Merchant { get; set; }
    public Guid? CategoryId { get; set; }
    public DateOnly Date { get; set; }
    public string? Note { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string? ReceiptBlobUri { get; set; }       // fase 4
    public ICollection<ExpenseLine> Lines { get; set; } = [];  // fase 4, da scontrino — storico prezzi
}

public class ExpenseLine
{
    public Guid Id { get; set; }
    public Guid ExpenseId { get; set; }
    public string RawText { get; set; } = null!;       // "Latte intero 1L" — come estratto dallo scontrino
    public string NormalizedName { get; set; } = null!; // condivide ProductNameNormalizer con ShoppingItem
    public decimal Price { get; set; }                 // importo di riga stampato, non un prezzo unitario calcolato
}
```

`Amount` è `decimal(18,2)`. Con `double` gli arrotondamenti sulle somme mensili producono discrepanze visibili all'utente, ed è un bug che si scopre tardi.

`Currency` è copiata sulla spesa alla creazione, non solo dedotta dallo spazio: se un giorno lo spazio cambia valuta, lo storico non deve cambiare significato retroattivamente.

Il parsing dell'importo dal testo dipende dalla cultura dell'utente — `"12,50"` significa cose diverse in italiano e in inglese. Vedi [09-localizzazione.md](09-localizzazione.md).

**`ExpenseLine` e lo storico prezzi (Fase 4).** Una riga per prodotto scontrinato, popolata da `ReceiptVisionClient` quando riesce a leggere anche il prezzo di riga (non solo il nome del prodotto). `NormalizedName` riusa la stessa normalizzazione di `ShoppingItem.NormalizedName` (`ProductNameNormalizer`, in `Tessera.Core`) — la domanda "che prodotto è" è identica nei due casi, e vale la stessa scelta di restare superficiale (lowercase, trim, articolo) invece di normalizzare marca/formato. `Price` è l'importo stampato per quella riga: uno scontrino "3x latte 2,40" produce una riga a 2,40, non un prezzo unitario calcolato — la stessa semplificazione dichiarata sopra per `ShoppingItem`. Nessun `SpaceId` diretto: si risale allo spazio tramite `Expense`, che lo possiede già.

### Categorie di spesa: il caso ibrido

```csharp
public class Category
{
    public Guid Id { get; set; }
    public Guid? SpaceId { get; set; }                // null = categoria predefinita di sistema
    public string? ResourceKey { get; set; }          // "Category.Groceries" — se valorizzata, si localizza
    public string? Name { get; set; }                 // nome libero, per categorie create dall'utente
}
```

Le categorie predefinite sono **chiavi di risorsa** e vanno localizzate: un utente anglofono deve vedere "Groceries", non "Spesa". Quelle create dall'utente sono contenuto e non si traducono mai. `ResourceKey` valorizzata discrimina i due casi.

È l'unico punto del modello dove testo di interfaccia e contenuto utente convivono nella stessa tabella, e per questo va tenuto esplicito.

### Promemoria

```csharp
public class Reminder
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public string Text { get; set; } = null!;          // contenuto utente, non si traduce
    public DateTimeOffset DueAt { get; set; }          // istante assoluto
    public string TimeZoneId { get; set; } = null!;    // IANA, per rendere e per le ricorrenze
    public RecurrenceRule? Recurrence { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? AssignedToUserId { get; set; }        // null = tutto lo spazio
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }    // idempotenza dell'invio
}
```

`DueAt` è un istante assoluto (`DateTimeOffset` in UTC), ma `TimeZoneId` va conservato accanto: "ogni lunedì alle 9" è definito nel fuso dell'utente, e con il solo istante assoluto la ricorrenza sbaglia di un'ora al cambio d'ora. È lo stesso errore descritto in [09-localizzazione.md](09-localizzazione.md), qui in forma più insidiosa perché si manifesta due volte l'anno.

`NotifiedAt` serve all'idempotenza: il worker che scandisce i promemoria scaduti può girare più volte, e senza questo campo un riavvio produce notifiche duplicate.

`AssignedToUserId` nullable permette sia "ricordami" che "ricorda a noi": in uno spazio condiviso la differenza conta.

Il parsing della data in linguaggio naturale ("giovedì", "fra due settimane", "il 15") è il caso in cui il fast path si arrende presto — vedi [05-ottimizzazioni.md](05-ottimizzazioni.md).

### Note

```csharp
public class Note
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public string? Title { get; set; }
    public string Body { get; set; } = null!;          // contenuto utente, non si traduce
    public Guid CreatedByUserId { get; set; }
    public Guid? LastEditedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

Testo libero condiviso per spazio — la stessa idea delle liste generiche, ma senza la struttura a voci spuntabili: una nota è un blocco di testo, non un elenco. `Title` è opzionale: molte note sono un pensiero breve senza bisogno di un titolo.

Niente FK da `CreatedByUserId`/`LastEditedByUserId` verso `User`, per lo stesso motivo di `AddedByUserId` su `ShoppingItem` — la resa passa sempre da `ResolveActorName` (vedi sotto).

### Spese ricorrenti

```csharp
public class RecurringExpense
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Description { get; set; } = null!;
    public Guid? CategoryId { get; set; }
    public RecurrenceRule Recurrence { get; set; } = null!;
    public DateOnly? EndsOn { get; set; }
    public bool AutoRegister { get; set; } = true;    // crea la Expense, o solo promemoria
    public DateOnly? LastGeneratedFor { get; set; }   // idempotenza della generazione
}
```

`LastGeneratedFor` evita la doppia registrazione: il job che genera le spese del mese controlla questo campo, non "esiste già una spesa simile".

`AutoRegister = false` degrada la ricorrenza a promemoria — utile per le bollette a importo variabile, dove conosci la scadenza ma non la cifra.

### Budget

```csharp
public class Budget
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid? CategoryId { get; set; }             // null = budget complessivo
    public decimal MonthlyLimit { get; set; }
    public int AlertThresholdPercent { get; set; } = 80;
    public DateOnly? LastAlertedFor { get; set; }     // un avviso per mese, non per spesa
}
```

`LastAlertedFor` è il campo che evita di trasformare il budget in spam: superata la soglia, l'avviso parte una volta per periodo, non a ogni spesa registrata.

Il budget per categoria funziona solo se la categorizzazione ha basso attrito — vedi la sezione seguente.

### Categorizzazione a basso attrito

È il punto in cui il tracciamento delle spese riesce o fallisce. Se categorizzare costa un tap in più per ogni spesa, dopo due settimane nessuno lo fa più e le aggregazioni per categoria diventano inutili — insieme al budget, che ne dipende.

Strategia, in ordine di precedenza:

```
1. Mappa merchant → categoria appresa per spazio     "Esselunga" → Spesa
2. Dedotta dagli articoli                            spesa nata dalla lista
3. Categoria più frequente per importo simile        euristica debole, solo come suggerimento
4. Domanda con inline keyboard                       e la risposta alimenta il punto 1
```

```csharp
public class MerchantCategoryMapping
{
    public Guid SpaceId { get; set; }
    public string MerchantNormalized { get; set; } = null!;   // lowercase, trim
    public Guid CategoryId { get; set; }
    public int ConfirmationCount { get; set; }                // cresce a ogni conferma
}
```

La mappa è **per spazio**, non globale: "Conad" può essere spesa alimentare per una famiglia e altro per un'altra. Il punto 4 non è un fallimento ma il meccanismo di apprendimento — chiesto una volta, poi mai più per quel merchant.

Obiettivo di progetto: dopo un mese d'uso, la domanda al punto 4 deve comparire raramente. Se compare ancora spesso, la normalizzazione del merchant è troppo rigida.

## Entità di supporto

### Ricorrenza

```csharp
public class RecurrenceRule
{
    public RecurrenceFrequency Frequency { get; set; }   // Daily, Weekly, Monthly, Yearly
    public int Interval { get; set; } = 1;               // ogni N periodi
    public DayOfWeek[]? DaysOfWeek { get; set; }         // per Weekly
    public int? DayOfMonth { get; set; }                 // per Monthly
}
```

Deliberatamente più povero di RRULE (RFC 5545). Copre i casi reali di promemoria e spese fisse, e non trascina la complessità di un motore di ricorrenze completo. Se in Fase 2 serve interoperare con gli eventi ricorrenti di Google e Microsoft, quella è una conversione a parte — non un motivo per adottare RRULE qui.

Attenzione al caso `DayOfMonth = 31`: nei mesi corti va deciso se si genera l'ultimo giorno o si salta. Scelta consigliata: ultimo giorno del mese, che è il comportamento atteso per una bolletta.

### Non-obiettivo: divisione delle spese

Il modello è **tracciamento di cassa comune**, non divisione dei debiti. Un solo bilancio per spazio, nessuna quota, nessun saldo fra membri.

| Modello | Domanda a cui risponde | Adottato |
|---|---|---|
| Tracciamento | "quanto ha speso la famiglia in spesa a gennaio" | **Sì** |
| Divisione | "Marco ha pagato 40€ per tutti, gli devo 10€" | No |

La divisione richiederebbe `ExpenseShare`, saldi per coppia di utenti e un flusso di pareggio: è un dominio a sé, non un campo in più. Ed è il territorio di Splitwise e Tricount, dove il confronto è diretto e sfavorevole.

Va scritto come non-obiettivo perché è un confine che si erode da solo: arriva la cena con gli amici, sembra naturale aggiungere "chi deve quanto a chi", e si finisce a costruire Splitwise dentro l'assistente. Se emergesse come bisogno reale, va affrontato come decisione consapevole di aprire un dominio nuovo — non come estensione di `Expense`.

### Calendari: uno per account non basta

Un singolo account Google o Microsoft espone **molti** calendari: il personale, quello famiglia condiviso con il partner, compleanni, festività, eventuali calendari di lavoro. La granularità della condivisione deve quindi stare sul **calendario**, non sull'account.

Il collegamento OAuth (`LinkedAccount`) è la concessione; i calendari sono le risorse che quella concessione rende visibili.

```csharp
public class ExternalCalendar
{
    public Guid Id { get; set; }
    public Guid LinkedAccountId { get; set; }
    public string ProviderCalendarId { get; set; } = null!;  // "primary", "xxx@group.calendar.google.com"
    public string Name { get; set; } = null!;                // dal provider, non tradotto
    public string? Color { get; set; }
    public bool IsPrimary { get; set; }
    public ProviderAccessRole ProviderRole { get; set; }     // cosa il provider consente
    public bool IsEnabled { get; set; }                      // l'utente l'ha attivato in console
    public DateTimeOffset? LastSyncedAt { get; set; }
}

public enum ProviderAccessRole
{
    FreeBusyReader = 1,   // solo fasce occupate
    Reader         = 2,
    Writer         = 3,
    Owner          = 4
}
```

`ProviderRole` viene dal provider e **non è negoziabile**: se il calendario famiglia è condiviso con te in sola lettura, nessun permesso interno può renderlo scrivibile. Va conservato per non proporre in console azioni che falliranno.

`IsEnabled` esiste perché la lista dei calendari è quasi sempre inquinata: "Compleanni", "Festività in Italia", calendari di iscrizioni dimenticate. **Default consigliato: solo il primario abilitato**, gli altri da attivare esplicitamente. Abilitarli tutti produce una vista unita inutilizzabile al primo collegamento, che è il momento in cui l'utente giudica la funzione.

### Mappatura calendario → spazio

Ogni calendario abilitato può essere esposto a uno o più spazi, con un livello proprio.

```csharp
public class CalendarSpaceMapping
{
    public Guid ExternalCalendarId { get; set; }
    public Guid SpaceId { get; set; }
    public CalendarShareLevel ShareLevel { get; set; }
    public bool IsDefaultWriteTarget { get; set; }   // dove finiscono gli eventi creati dal bot
}

public enum CalendarShareLevel
{
    Availability = 1,   // solo fasce occupate, nessun titolo — freebusy
    Details      = 2,   // titoli e orari visibili ai membri
    Write        = 3    // i membri possono creare eventi qui
}
```

Lo scenario concreto:

| Calendario | Spazio "Personale" | Spazio "Casa" | Spazio "Calcetto" |
|---|---|---|---|
| Mio personale (Google) | Details + Write | Availability | Availability |
| Famiglia (condiviso con moglie) | Details | Details + Write | non mappato |
| Lavoro (Microsoft) | Details | Availability | Availability |
| Compleanni | `IsEnabled = false` | — | — |

Nota la prima riga: il tuo calendario personale è visibile in dettaglio solo a te, mentre in "Casa" tua moglie ne vede la disponibilità — abbastanza per trovare quando siete liberi, non abbastanza per leggere i titoli. È esattamente il caso d'uso di `AccessLevel.Availability`.

**Il livello effettivo è il minimo fra tre vincoli**, e va calcolato in un solo punto del codice:

```
effettivo = min(ProviderRole, CalendarShareLevel, MembershipPermission[Calendar])
```

Se il provider concede `Reader`, la mappatura dice `Write` e il membro ha `Write`, il risultato è lettura. Sbagliare questo calcolo significa proporre all'utente azioni che il provider rifiuterà, o peggio esporre dettagli che dovevano restare disponibilità.

`IsDefaultWriteTarget` risolve la domanda "dove creo l'evento?": in uno spazio con tre calendari scrivibili serve un bersaglio predefinito, o si ricade su una domanda a ogni creazione.

### Il gotcha: calendario condiviso collegato due volte

Tu e tua moglie avete entrambi accesso al calendario "Famiglia". Se entrambi collegate il vostro account e abilitate quel calendario, lo spazio "Casa" lo vede **due volte** — e la vista unita mostra ogni evento duplicato, o peggio conta due volte le fasce occupate nel calcolo delle disponibilità.

È lo scenario normale, non il caso limite: un calendario condiviso in famiglia è precisamente ciò che entrambi collegheranno.

La deduplica va fatta sull'identità dell'evento presso il provider, non sul nome del calendario:

- **Google**: `iCalUID` è stabile fra le copie dello stesso evento
- **Microsoft**: `iCalUId` (attenzione alla maiuscola diversa) ha la stessa proprietà

```csharp
// nella composizione della vista unita
var events = raw.GroupBy(e => e.ICalUid ?? $"{e.CalendarId}:{e.Id}")
                .Select(g => g.First());
```

Il fallback su `{CalendarId}:{Id}` serve per gli eventi senza UID, che esistono in casi particolari.

**Dedurre lo stesso calendario in anticipo è meglio che deduplicare gli eventi dopo.** In console, quando un secondo membro abilita un calendario che nello spazio è già presente, vale la pena segnalarlo: "Questo calendario è già collegato da Alessio in Casa" con l'opzione di non aggiungerlo. Evita il problema alla radice invece di correggerlo a ogni query.

### Uscita e rimozione da uno spazio

Un membro che esce lascia dietro di sé riferimenti: voci in lista, spese registrate, promemoria creati. Vanno gestiti, o lo storico diventa incoerente.

**La membership si cancella, i riferimenti restano.** `AddedByUserId` e `CreatedByUserId` continuano a puntare all'utente: le spese di gennaio devono restare attribuite anche se la persona è uscita in febbraio, o le aggregazioni storiche cambiano retroattivamente. Nella resa si mostra il nome se l'utente esiste ancora, altrimenti un'etichetta localizzata ("Ex membro").

Cosa va invece rimosso subito:

| Elemento | Azione all'uscita |
|---|---|
| `Membership` e `MembershipPermission` | Cancellati |
| `CalendarSpaceMapping` dei suoi calendari verso quello spazio | **Cancellati** — critico: altrimenti lo spazio continua a vedere il suo calendario |
| `LastOperation`, `ConversationState` per quello spazio | Cancellati |
| `User.DefaultSpaceId` se puntava a quello spazio | Azzerato |
| Notifiche pendenti verso di lui per quello spazio | Annullate |
| Voci, spese, promemoria creati da lui | **Conservati** |

La seconda riga è quella che si dimentica e che è un problema di privacy reale: se un membro esce ma la mappatura del suo calendario resta, lo spazio continua a leggere la sua disponibilità.

**Il caso dell'ultimo Admin.** Un `Admin` non può uscire lasciando lo spazio senza amministratore: diventerebbe ingestibile — nessuno potrebbe più invitare, cambiare permessi o eliminarlo. Regole:

1. Se ci sono altri membri, l'uscita richiede di **trasferire il ruolo** a uno di loro
2. Se è l'unico membro, l'uscita equivale a eliminare lo spazio, e va confermata come tale
3. Lo spazio "Personale" non si può abbandonare, solo eliminare insieme all'account

**Rimozione da parte di un Admin** segue le stesse regole sui dati, con una differenza di comunicazione: la persona rimossa va informata ("Non fai più parte di *Casa*"), altrimenti scoprirebbe il cambiamento solo trovando il bot che non risponde più — e sospetterebbe un malfunzionamento.

### L'ex membro: come renderlo

Cancellata la `Membership`, ogni voce e ogni spesa create da quella persona hanno un `AddedByUserId` che non corrisponde più a un membro. Senza una soluzione, la console mostra un GUID o un nome vuoto, e le domande legittime ("chi ha registrato questa spesa?") non hanno risposta.

Servono due casi distinti, perché hanno soluzioni diverse:

| Caso | L'utente esiste ancora? | Soluzione |
|---|---|---|
| Uscito o rimosso da uno spazio | Sì, ha ancora l'account | Archivio della membership |
| Account eliminato del tutto | No | Pseudonimizzazione |

#### Caso 1 — uscito dallo spazio

```csharp
public class MembershipArchive
{
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayNameSnapshot { get; set; } = null!;  // nome al momento dell'uscita
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset LeftAt { get; set; }
    public MembershipEndReason Reason { get; set; }           // Left | Removed | AccountDeleted
}
```

`DisplayNameSnapshot` è la chiave: si conserva il nome **come era allora**, non un riferimento vivo. Così la spesa di gennaio resta attribuita a "Marco" anche se Marco ha poi cambiato nome o eliminato l'account, e la resa non richiede di leggere il record `User` — che potrebbe non esistere più.

La resa in console e nel bot diventa:

```csharp
public string ResolveActorName(Guid spaceId, Guid userId) =>
    activeMembers.TryGetValue(userId, out var m) ? m.DisplayName
  : archive.TryGet(spaceId, userId, out var a) ? a.DisplayNameSnapshot   // es. "Marco"
  : localizer["Space.FormerMember"];                                     // "Ex membro"
```

Lo snapshot va mostrato **senza etichetta aggiuntiva nelle liste**: "Speso da Marco" è corretto e leggibile. L'informazione che non è più membro serve solo dove è rilevante — nella pagina dei membri dello spazio, in una sezione separata "Ex membri" con la data di uscita.

**Cosa l'ex membro non deve più poter fare**, e va verificato esplicitamente perché è facile lasciare un varco:

- Nessuna notifica dallo spazio, mai più
- `/undo` su operazioni fatte in quello spazio: rifiutato
- Se avesse un `ActiveSpaceId` o un `DefaultSpaceId` puntato lì, azzerati
- I suoi calendari non più leggibili (`CalendarSpaceMapping` cancellata — vedi sopra)
- Riferimenti allo spazio nella sua ricerca storica: esclusi

Quest'ultimo punto è quello meno ovvio. "Quanto abbiamo speso l'anno scorso?" fatto da un ex membro **non deve** includere lo spazio da cui è uscito, anche per il periodo in cui ne faceva parte. La regola è che la visibilità segue la membership attuale, non quella storica: più semplice da implementare e più difendibile sul piano della privacy.

#### Caso 2 — account eliminato

Qui c'è una tensione reale fra due esigenze legittime: il diritto alla cancellazione dell'interessato e l'integrità dello storico condiviso di altre persone. Le spese di gennaio dello spazio "Casa" appartengono anche agli altri membri, e cancellarle altererebbe i loro dati.

**La risposta è la pseudonimizzazione, non la cancellazione a cascata.** Alla cancellazione dell'account:

| Dato | Azione |
|---|---|
| `User` (email, nome, preferenze) | **Cancellato** |
| `ChannelIdentity`, `LinkedAccount`, token in Key Vault | **Cancellati e revocati** presso il provider |
| `ExternalCalendar`, `CalendarSpaceMapping` | **Cancellati** |
| `MembershipArchive.DisplayNameSnapshot` | Sostituito con un segnaposto localizzato |
| `AddedByUserId` sulle risorse condivise | **Conservato** come GUID orfano, non più risolvibile a una persona |
| Risorse nel suo spazio "Personale" | **Cancellate** — nessun altro ne è titolare |
| Contenuti liberi che ha scritto (note di spesa) | Da valutare: possono contenere dati personali |

Il GUID orfano non è più un dato personale: non è collegabile a una persona identificabile, perché ogni riferimento identificativo è stato rimosso. Questo è ciò che rende la soluzione compatibile con la cancellazione mantenendo coerenti i totali degli altri membri.

L'ultima riga merita attenzione: una nota su una spesa può contenere testo libero personale. Non c'è una regola automatica — l'informativa deve dichiarare che i contenuti inseriti in uno spazio condiviso restano visibili ai membri anche dopo la cancellazione dell'account, e vale la pena offrirne l'eliminazione selettiva prima di chiudere.

Dettagli sugli adempimenti in [07-compliance.md](07-compliance.md).

### Token di collegamento bot ↔ console

```csharp
public class LinkToken
{
    public Guid Id { get; set; }
    public string Token { get; set; } = null!;        // random 32 byte, base64url
    public Guid UserId { get; set; }
    public string ChannelName { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }     // +10 minuti
    public DateTimeOffset? ConsumedAt { get; set; }
}
```

Questo token **è un'autenticazione**: chi lo possiede associa la propria chat al tuo account. Monouso, scadenza breve, generato solo da sessione autenticata. Vedi il flusso in [03-integrazioni.md](03-integrazioni.md).

### Idempotenza e conversazione

```csharp
public class ProcessedMessage
{
    public string ChannelName { get; set; } = null!;
    public string ProviderMessageId { get; set; } = null!;
    public DateTimeOffset ProcessedAt { get; set; }
    // chiave primaria composta (ChannelName, ProviderMessageId)
}

public class ConversationState
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? ActiveSpaceId { get; set; }          // spazio disambiguato di recente
    public string? PendingIntent { get; set; }        // conferme in due passaggi
    public string StateJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }     // TTL breve, 30 min
}
```

Lo stato conversazionale è volatile: serve per "quale delle due riunioni intendi?" e va scaduto aggressivamente. Non è la memoria a lungo termine del bot — quella è il database di dominio.

`ActiveSpaceId` è il punto 2 della catena di disambiguazione descritta sopra: evita di richiedere lo spazio a ogni messaggio dopo che l'utente l'ha già indicato. È nullable perché nella maggioranza dei casi la disambiguazione non serve.

### Undo

```csharp
public class LastOperation
{
    public Guid UserId { get; set; }                   // chiave primaria: una per utente
    public Guid SpaceId { get; set; }
    public string OperationType { get; set; } = null!; // "shopping.add", "expense.create"
    public string UndoPayloadJson { get; set; } = null!;
    public DateTimeOffset PerformedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }      // +10 minuti
    public bool IsUndone { get; set; }
}
```

Una sola operazione per utente, non uno stack: "annulla" tre volte in chat è confusionario e apre problemi di consistenza su risorse condivise.

**L'undo è per utente, non per spazio.** Se due membri operano sulla stessa lista, "annulla" deve toccare l'operazione di chi lo scrive. Diversamente diventa imprevedibile.

`UndoPayloadJson` contiene quanto serve per invertire l'operazione — nel caso dello svuotamento della lista, tutte le voci cancellate. È l'undo che salva più situazioni: senza, un `/list clear` accidentale è perdita di dati.

Il razionale completo, la superficie di attivazione e la correzione contestuale sono in [10-conversazione.md](10-conversazione.md).

### Suggerimenti di onboarding

```csharp
public class OnboardingHint
{
    public Guid UserId { get; set; }
    public string HintKey { get; set; } = null!;       // "expenses.intro", "sharing.invite"
    public int ShownCount { get; set; }
    public bool Dismissed { get; set; }                // "più tardi" definitivo
    public DateTimeOffset? LastShownAt { get; set; }
}
```

Serve perché i suggerimenti contestuali devono **ritirarsi**: un bot che continua a spiegarsi diventa rumore, e un invito ripetuto è la cosa che lo fa silenziare. `Dismissed` va rispettato a lungo, non per una sessione.

## Indici e vincoli

```csharp
// ChannelIdentity: lookup su ogni messaggio in ingresso, è il percorso più caldo
builder.Entity<ChannelIdentity>()
    .HasIndex(x => new { x.ChannelName, x.ExternalUserId }).IsUnique();

builder.Entity<Membership>()
    .HasIndex(x => new { x.SpaceId, x.UserId }).IsUnique();

builder.Entity<MembershipPermission>()
    .HasKey(x => new { x.MembershipId, x.Resource });

builder.Entity<ProcessedMessage>()
    .HasKey(x => new { x.ChannelName, x.ProviderMessageId });

builder.Entity<LinkToken>()
    .HasIndex(x => x.Token).IsUnique();

builder.Entity<ShoppingItem>()
    .HasIndex(x => new { x.ShoppingListId, x.IsChecked });

builder.Entity<Expense>()
    .HasIndex(x => new { x.SpaceId, x.Date });   // aggregazioni mensili

// promemoria da notificare: query più frequente del worker schedulato
builder.Entity<Reminder>()
    .HasIndex(x => new { x.IsCompleted, x.DueAt })
    .HasFilter("[IsCompleted] = 0");

builder.Entity<RecurringExpense>()
    .HasIndex(x => new { x.SpaceId, x.LastGeneratedFor });

builder.Entity<MerchantCategoryMapping>()
    .HasKey(x => new { x.SpaceId, x.MerchantNormalized });

// Restrict, non Cascade: cancellare un piano non deve cancellare a cascata gli spazi che lo
// referenziano — deve fallire finché quegli spazi non vengono spostati su un altro piano.
builder.Entity<Space>()
    .HasOne<SubscriptionPlan>()
    .WithMany()
    .HasForeignKey(x => x.PlanId)
    .IsRequired()
    .OnDelete(DeleteBehavior.Restrict);

builder.Entity<Budget>()
    .HasIndex(x => new { x.SpaceId, x.CategoryId }).IsUnique();

builder.Entity<LastOperation>()
    .HasKey(x => x.UserId);              // una sola operazione annullabile per utente

builder.Entity<OnboardingHint>()
    .HasKey(x => new { x.UserId, x.HintKey });

builder.Entity<ExternalCalendar>()
    .HasIndex(x => new { x.LinkedAccountId, x.ProviderCalendarId }).IsUnique();

builder.Entity<CalendarSpaceMapping>()
    .HasKey(x => new { x.ExternalCalendarId, x.SpaceId });

builder.Entity<MembershipArchive>()
    .HasKey(x => new { x.SpaceId, x.UserId });

builder.Entity<Expense>()
    .Property(x => x.Amount).HasPrecision(18, 2);
```

**Nessuna foreign key da `AddedByUserId`, `CheckedByUserId`, `CreatedByUserId` verso `User`.** È una scelta deliberata, non una dimenticanza: con una FK vincolante, la cancellazione di un account fallirebbe finché esistono spese o voci che lo referenziano, e l'unica alternativa sarebbe `ON DELETE CASCADE` — che cancellerebbe lo storico condiviso degli altri membri.

Lasciando il GUID senza vincolo, la cancellazione dell'account funziona e il riferimento diventa orfano per costruzione, che è esattamente il comportamento voluto (vedi sopra). Il prezzo è che l'integrità va garantita dal codice: la resa del nome deve **sempre** passare da `ResolveActorName` e gestire il caso non risolvibile, mai assumere che una join produca un risultato.

Lo stesso vale per `MembershipArchive.UserId`.

## Autorizzazione come servizio di dominio

Il controllo dei permessi vive in `Tessera.Core`, senza dipendenze da EF, e va testato unitariamente. È il posto dove un errore diventa una fuga di dati fra membri di uno spazio.

```csharp
public interface IAccessPolicy
{
    Task<bool> CanAsync(Guid userId, Guid spaceId, ResourceKind resource, AccessLevel required);
}
```

Regola: **ogni** query di dominio filtra per spazi accessibili all'utente, non si limita a controllare il permesso a valle. Un `WHERE SpaceId IN (spazi dell'utente)` applicato sistematicamente — via query filter EF o repository dedicati — è più robusto di un controllo dimenticabile per singolo handler.

## SQL o Cosmos?

**Azure SQL.** Il modello è relazionale (join fra membership, spazi, risorse), servono aggregazioni (`SUM` per mese e categoria) e transazioni. Cosmos costringerebbe a denormalizzare la membership in ogni documento, con il problema del fan-out quando un permesso cambia.

Costo: vedi [04-costi.md](04-costi.md). Il tier serverless di Azure SQL con auto-pause è la scelta per l'MVP, con l'avvertenza che il primo accesso dopo una pausa ha latenza di alcuni secondi — accettabile, ma va tenuto presente se il bot sembra "lento al mattino".
