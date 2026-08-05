# 09 — Localizzazione multilingua

## Le due decisioni da prendere ora

Il resto della localizzazione è incrementale: aggiungere una lingua alle risorse è lavoro meccanico. Due cose invece vanno fissate prima di scrivere codice, perché cambiarle dopo rompe l'esperienza di chi ha già usato il bot.

1. **I nomi dei comandi Telegram sono canonici in inglese**, non localizzati. Rinominarli dopo confonde chi li ha imparati.
2. **Le notifiche sono eventi strutturati resi per destinatario**, non stringhe generate e inoltrate. Recuperarlo dopo significa riscrivere il sistema di notifica.

## Lingue supportate

| Livello | Lingue | Comportamento |
|---|---|---|
| **Completo** | Italiano, Inglese | UI localizzata + fast path L1/L2 del router |
| **Parziale** | Tutte le altre | Risposte via LLM (nativamente multilingua), UI in fallback inglese |

Scelta deliberata: il fast path deterministico esiste **solo** per le lingue che si è in grado di valutare. Vedi la sezione sul router.

## Rilevamento e preferenza della lingua

Telegram fornisce `Message.From.LanguageCode` — un tag IETF (`it`, `en-US`, `de`). È la lingua del **client**, non necessariamente quella in cui l'utente vuole parlare: un italiano con Telegram in inglese esiste.

Va quindi usato come **default alla creazione dell'utente**, non come verità permanente.

```csharp
public class User
{
    // ...
    public string PreferredCulture { get; set; } = "en";   // IETF: "it", "en", "de"
    public string? TimeZoneId { get; set; }                // IANA: "Europe/Rome"
}
```

Precedenza:

```
1. User.PreferredCulture             ← impostata da console o da /language
2. Message.From.LanguageCode         ← default alla prima interazione
3. "en"                              ← fallback
```

WhatsApp non fornisce un equivalente affidabile della lingua del client: lì il default è `"en"` e la scelta va fatta in console o al primo contatto.

`TimeZoneId` sta accanto a `PreferredCulture` perché è lo stesso tipo di problema e si dimentica nello stesso modo. Lingua e fuso sono indipendenti: un italiano a Londra vuole l'interfaccia in italiano e gli orari in `Europe/London`.

## Il router L2 è per lingua

Il problema strutturale: le regex del fast path non sono traducibili. `aggiungi|metti|segna` e `add|put|remember` non hanno struttura comune, e nemmeno lo stesso ordine di parole.

```csharp
public interface IIntentMatcher
{
    string Intent { get; }
    string Culture { get; }                     // "it", "en"
    IntentMatch? TryMatch(string text);
}

public class IntentRouter(IEnumerable<IIntentMatcher> matchers)
{
    private readonly Dictionary<string, IIntentMatcher[]> _byCulture =
        matchers.GroupBy(m => m.Culture)
                .ToDictionary(g => g.Key, g => g.ToArray());

    public IntentMatch? TryRoute(string text, string culture)
    {
        var lang = culture.Split('-')[0];        // "en-US" → "en"
        if (!_byCulture.TryGetValue(lang, out var set))
            return null;                        // lingua non coperta → L3
        // ...
    }
}
```

### Perché non aggiungere matcher per tutte le lingue

Sembra un guadagno e non lo è. Un `IIntentMatcher` in una lingua che non si parla è impossibile da valutare: non si sa se `"dodaj mleko"` copre le formulazioni reali o se produce falsi positivi su frasi che significano altro. E un falso positivo del fast path è **peggio** di nessun matcher: esegue l'azione sbagliata in silenzio, invece di passare a un modello che avrebbe capito.

Il costo di rinunciare al fast path è qualche centesimo in più per utente non-IT/EN. Il costo di un matcher sbagliato è la fiducia dell'utente.

Regola: **un matcher si aggiunge solo per una lingua di cui si può scrivere e giudicare il corpus di test.**

### Corpus di test per lingua

```csharp
public static TheoryData<string, string, string?> Corpus => new()
{
    // italiano — L2
    { "it", "aggiungi il latte",                  "shopping.add" },
    { "it", "metti 2 litri di latte nella lista",  "shopping.add" },
    { "it", "quanto ho speso a gennaio",           "expenses.query" },
    // italiano — deve cadere a L3
    { "it", "ricordati che serve il pane",         null },
    { "it", "finito il detersivo",                 null },

    // inglese — L2
    { "en", "add milk",                            "shopping.add" },
    { "en", "put bread on the list",               "shopping.add" },
    { "en", "how much did I spend in January",     "expenses.query" },
    // inglese — deve cadere a L3
    { "en", "we're out of detergent",              null },

    // lingua non coperta — sempre L3
    { "de", "milch hinzufügen",                    null },
};
```

Il corpus cresce osservando i log: ogni frase che finisce a L3 quando poteva essere gestita a L2 va aggiunta.

## Comandi Telegram: canonici, descrizioni localizzate

`setMyCommands` accetta un `language_code`, quindi tecnicamente si potrebbe mostrare `/lista` agli italiani e `/list` agli inglesi. **Non farlo.**

Il problema è il gruppo misto: due membri dello stesso spazio vedono menu diversi, e un comando copiato da una conversazione all'altra non funziona. In una lista della spesa condivisa fra persone di lingue diverse è uno scenario normale, non un caso limite.

### Schema adottato

| Elemento | Lingua |
|---|---|
| Nome del comando | **Canonico inglese**: `/list`, `/expense`, `/month`, `/link`, `/language`, `/help` |
| Descrizione nel menu | Localizzata via `setMyCommands` con `language_code` |
| Alias localizzati | Accettati dal router, **non** mostrati nel menu |

```csharp
// registrazione: nomi identici, descrizioni per lingua
await bot.SetMyCommands([
    new("list",     "Show the shopping list"),
    new("expense",  "Record an expense"),
    new("month",    "Monthly summary"),
    new("link",     "Link your account"),
    new("language", "Change language"),
    new("help",     "Help"),
]);   // default: inglese

await bot.SetMyCommands([
    new("list",     "Mostra la lista della spesa"),
    new("expense",  "Registra una spesa"),
    new("month",    "Riepilogo mensile"),
    new("link",     "Collega il tuo account"),
    new("language", "Cambia lingua"),
    new("help",     "Aiuto"),
], languageCode: "it");
```

Gli alias localizzati funzionano senza comparire nel menu — un utente italiano che scrive `/lista` per abitudine ottiene il risultato giusto:

```csharp
private static readonly Dictionary<string, string> CommandAliases = new(StringComparer.OrdinalIgnoreCase)
{
    ["lista"]  = "list",     ["spesa"]  = "expense",
    ["mese"]   = "month",    ["collega"] = "link",
    ["lingua"] = "language", ["aiuto"]  = "help",
};
```

`/language` merita di stare nel menu: è il comando che serve proprio a chi ha ricevuto il default sbagliato, e cercarlo in console è attrito.

## LLM: prompt in inglese, risposta nella lingua dell'utente

Il system prompt e le `description` dei tool restano **sempre in inglese**, in un'unica versione.

```csharp
var system = """
    You are a personal tessera for shopping lists, expenses and calendars.
    Always reply in the user's language, indicated below.
    Keep replies short and conversational — this is a chat, not a document.
    """;

var userContext = $"User language: {user.PreferredCulture}. Timezone: {user.TimeZoneId}.";
```

### Perché non tradurre il prompt

Due ragioni, la seconda decisiva:

1. Le `description` dei tool sono lette dal modello, non dall'utente. Tradurle non aggiunge nulla e introduce imprecisioni.
2. **Il prompt caching si romperebbe.** La parte cacheable deve essere identica byte per byte. Un system prompt per lingua significa una cache per lingua, e con pochi utenti per lingua la cache non si scalda mai. Vedi [05-ottimizzazioni.md](05-ottimizzazioni.md).

L'indicazione della lingua va quindi nella **coda variabile** del prompt, dopo la parte statica, insieme a fuso orario e contesto dello spazio.

I modelli attuali rispettano bene questa istruzione. Il caso da verificare è la risposta mista: utente che scrive in inglese in uno spazio con nomi di articoli in italiano. La regola da mettere nel prompt è che il **contenuto dell'utente si cita nella lingua originale**, si traduce solo il testo generato.

## Notifiche in uno spazio multilingua

Il caso concreto: tu scrivi in italiano, un membro dello spazio è anglofono. La notifica deve arrivargli in inglese.

Questo significa che una notifica **non può** essere una stringa già composta e inoltrata a tutti. Deve essere un evento strutturato, reso nella lingua di ciascun destinatario.

```csharp
// evento di dominio: nessun testo, solo fatti
public record ShoppingItemAdded(
    Guid SpaceId,
    Guid ActorUserId,
    string ItemText,
    string? OriginChatId,          // dove è nata l'azione: serve a non notificare lì
    DateTimeOffset At);

// resa per destinatario
foreach (var recipient in await GetNotifiableMembers(evt.SpaceId, evt.ActorUserId))
{
    var text = localizer.For(recipient.PreferredCulture)[
        "Notification.ItemAdded",     // "{0} ha aggiunto {1}" / "{0} added {1}"
        actorName,
        evt.ItemText];

    await channel.SendTextAsync(recipient.Address, text, ct);
}
```

### Dove non inviare la notifica

Prima di localizzare una notifica, va deciso **se inviarla**. La regola: mai notificare nel canale dove l'azione è già visibile.

| Origine dell'azione | Destinatario | Notifica |
|---|---|---|
| Gruppo "Casa" | Membri di "Casa" presenti nel gruppo | **No** — l'hanno già vista scorrere |
| Gruppo "Casa" | Membro di "Casa" non nel gruppo | Sì, in privato |
| Chat privata di A, su spazio "Casa" | Gruppo "Casa" | Sì, una volta nel gruppo |
| Chat privata di A, su spazio "Casa" | Chat privata di B | No, se B è nel gruppo già notificato |
| Qualsiasi | L'autore dell'azione | **Mai** |

Le due righe che contano: notificare nel gruppo un'azione fatta nel gruppo è rumore puro, e notificare due volte lo stesso utente (nel gruppo e in privato) è il modo più rapido per farsi silenziare.

Implementazione: la resa della notifica riceve l'`InboundMessage.ExternalChatId` di origine e salta i destinatari raggiungibili tramite quella chat. In pratica, se lo spazio ha un `GroupChatId` e l'azione è nata lì, si notifica solo chi non è membro del gruppo Telegram.

`ShoppingItemAdded.ActorUserId` esiste anche per questo: l'autore non si notifica mai.

### La distinzione che va tenuta rigida

| Cosa | Si traduce? |
|---|---|
| Testo dell'interfaccia, notifiche, messaggi di errore | **Sì** |
| `ShoppingItem.RawText` ("latte", "carta igienica") | **No** |
| `Expense.Merchant`, `Expense.Note` | **No** |
| `Space.Name` ("Casa") | **No** |
| `MembershipArchive.DisplayNameSnapshot` | **No** — è un nome proprio |
| Etichetta "Ex membro" quando lo snapshot non esiste | **Sì** — è testo di interfaccia |
| Titoli degli eventi di calendario | **No** |
| Nomi delle categorie di spesa predefinite | **Sì** (sono chiavi, non contenuto) |

"Latte" resta "latte" anche in una notifica in inglese: è contenuto dell'utente, non interfaccia. Se questa distinzione si sfuma, si finisce a tradurre la lista della spesa — e un articolo tradotto non si ritrova più nello storico.

Le categorie di spesa sono il caso ibrido: quelle predefinite (`Groceries`, `Transport`) sono chiavi di risorsa e si localizzano; quelle create dall'utente sono contenuto e no. Nel modello dati: `Category.ResourceKey` nullable — se è valorizzata si localizza, altrimenti si mostra `Category.Name`.

## Due gotcha che producono bug silenziosi

### 1. La cultura nel BackgroundService

Nel worker che consuma la coda **non c'è un HTTP context**, quindi nessun middleware ha impostato la cultura. `CultureInfo.CurrentUICulture` è quella del thread pool — tipicamente quella del server, o invariant.

Il risultato è che `IStringLocalizer` restituisce sempre il fallback inglese, e il bug è silenzioso: non solleva eccezioni, semplicemente tutto esce nella lingua sbagliata. In sviluppo su una macchina italiana può anche sembrare che funzioni.

La cultura va impostata esplicitamente all'inizio dell'elaborazione di **ogni** messaggio:

```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    await foreach (var msg in queue.ReadAllAsync(ct))
    {
        var user = await identity.ResolveAsync(msg);
        var culture = new CultureInfo(user.PreferredCulture);

        CultureInfo.CurrentCulture = culture;      // formati: numeri, date
        CultureInfo.CurrentUICulture = culture;    // risorse: testi

        await processor.HandleAsync(msg, user, ct);
    }
}
```

Attenzione: con questo schema la cultura è impostata **per iterazione**, e un'elaborazione parallela la rimescolerebbe. Se il worker diventa concorrente, la cultura va passata esplicitamente invece di stare nel contesto del thread — un `LocalizationContext` nel proprio scope DI è più robusto. Con un consumer sequenziale in Fase 1 lo schema sopra è sufficiente.

**Le notifiche sono l'eccezione anche qui**: si rendono nella cultura del *destinatario*, non in quella impostata per il messaggio in elaborazione. Il localizer va invocato con la cultura esplicita, non tramite il contesto del thread.

### 2. Parsing degli importi

`"12,50"` in italiano è dodici euro e cinquanta. In inglese la virgola è separatore di migliaia, e `12,50` è ambiguo o vale 1250.

```csharp
// il parsing usa la cultura dell'utente
decimal.TryParse(input, NumberStyles.Currency | NumberStyles.AllowDecimalPoint,
                 new CultureInfo(user.PreferredCulture), out var amount);
```

Nei casi ambigui — `"1,5"` o `"1.500"` — conviene far confermare l'importo con una inline keyboard invece di indovinare. Un errore di fattore 100 su una spesa è visibile e mina la fiducia in tutto il resto.

### Valuta e formato sono cose diverse

Errore classico: usare `PreferredCulture` per decidere anche la valuta.

| Proprietà | Di chi è |
|---|---|
| **Valuta** | Dello **spazio** (`Space.Currency`) — una spesa condivisa è in una valuta sola |
| **Formato** | Del **lettore** (`User.PreferredCulture`) |

Lo stesso importo si rende `1.234,56 €` per l'utente italiano e `€1,234.56` per l'anglofono. Sono la stessa spesa nella stessa valuta, mostrata secondo due convenzioni.

```csharp
public static string Format(decimal amount, string currency, string culture)
{
    var ci = (CultureInfo)new CultureInfo(culture).Clone();
    var region = new RegionInfo(currency == "EUR" ? "IT" : currency);
    ci.NumberFormat.CurrencySymbol = region.CurrencySymbol;
    return amount.ToString("C", ci);
}
```

Le date hanno lo stesso schema: il **fuso** è dell'utente (`TimeZoneId`), il **formato** della sua cultura. `05/08/2026` è il 5 agosto per un italiano e il 5 maggio per un americano — sulle date conviene il formato lungo (`"d MMMM yyyy"`) invece del numerico, che elimina l'ambiguità.

## Organizzazione delle risorse

```
src/Tessera.Core/Resources/
  Messages.resx        ← inglese, lingua di riferimento e fallback
  Messages.it.resx
```

Chiavi semantiche e non testo inglese come chiave, così un cambio di formulazione non tocca il codice:

```
Shopping.ItemAdded          = "{0} added {1}"
Shopping.ListEmpty          = "The list is empty"
Shopping.ItemChecked        = "{0} checked off {1}"
Expenses.MonthlyTotal       = "In {0} you spent {1}"
Errors.NotUnderstood        = "I didn't get that. Try /help"
Errors.NoPermission         = "You don't have permission for this in {0}"
Space.FormerMember          = "Former member"
Space.RemovedFromSpace      = "You're no longer part of {0}"
Link.Success                = "Linked as {0}"
```

`Errors.NotUnderstood` è la stringa che l'utente vede quando il router fallisce: vale la pena scriverla bene in ogni lingua, ed è anche la metrica più importante del prodotto. Vedi [05-ottimizzazioni.md](05-ottimizzazioni.md).

Configurazione:

```csharp
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture("en");
    o.SupportedCultures = o.SupportedUICultures =
        [new CultureInfo("en"), new CultureInfo("it")];
});
```

`RequestLocalizationOptions` copre solo la console web. Il worker dei messaggi ha bisogno della gestione esplicita descritta sopra.

## Console web

- Selettore di lingua nel profilo, che scrive `User.PreferredCulture` — la stessa proprietà usata dal bot: un solo posto, nessuna divergenza fra canale e web
- `RequestLocalizationProvider` custom che legge la preferenza dell'utente autenticato prima di `Accept-Language`
- Le pagine pubbliche (homepage, privacy policy) seguono `Accept-Language`, non essendoci un utente

**La privacy policy va tradotta in tutte le lingue supportate.** Non è un dettaglio estetico: la OAuth verification di Google la esamina, e per gli utenti UE l'informativa GDPR in una lingua non comprensibile è problematica. Se una lingua non ha la privacy policy tradotta, meglio non elencarla fra quelle supportate.

## Cosa rimandare

| Tentazione | Perché dopo |
|---|---|
| Traduzione automatica delle risorse via LLM | Genera testo plausibile e sbagliato nei dettagli. Meglio poche lingue curate. |
| Rilevamento della lingua dal testo del messaggio | `PreferredCulture` + `/language` bastano. Il rilevamento per messaggio produce oscillazioni fastidiose su frasi brevi (`"ok"`, `"pane"`). |
| Matcher L2 oltre italiano e inglese | Solo quando si può giudicare il corpus. Vedi sopra. |
| Pluralizzazione complessa | Italiano e inglese hanno due forme. Serve una vera gestione ICU solo con lingue slave o arabo in elenco. |

## Impatto sulla roadmap

L'infrastruttura di localizzazione va in **Fase 0-1**, non dopo: `PreferredCulture` nello schema, `IIntentMatcher` con dimensione cultura, notifiche come eventi strutturati, cultura esplicita nel worker.

Aggiungere una lingua dopo è lavoro meccanico. Recuperare le notifiche come eventi strutturati, o rinominare i comandi, non lo è.
