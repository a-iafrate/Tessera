# Copilot instructions — Tessera

**Tessera** is a family assistant bot for Telegram and WhatsApp with a Blazor web console: shared shopping lists, family expense tracking, reminders, calendars.

Detailed design lives in `docs/` (written in Italian). This file is the summary; consult `docs/` for rationale.

## Language

All code artifacts are **English**: identifiers, type and file names, comments, XML docs, commit messages, log and exception messages, test names, resource keys.

User-facing text is never hardcoded — it goes through `IStringLocalizer` with an English key. Italian translations live only in `Messages.it.resx`.

Files under `docs/` are in Italian and must not be translated.

## Stack

.NET 10, ASP.NET Core, Blazor Web App (interactive **server** render), EF Core with Azure SQL, Azure App Service, Key Vault with Managed Identity, Azure OpenAI (gpt-4o-mini), `Telegram.Bot`, `Microsoft.Graph`, `Google.Apis.Calendar.v3`.

Console and bot webhooks share a single ASP.NET host.

## Domain model

`User → Membership(per-resource permission) → Space → Resources`

Everything is shareable by construction. A personal resource is a Space with a single member — there is no separate "personal" code path.

Projects: `Tessera.Web` (host), `Tessera.Core` (domain, no infrastructure dependencies), `Tessera.Channels`, `Tessera.Integrations`, `Tessera.Ai`, `Tessera.Data`.

## Rules to follow when suggesting code

- Every resource has a non-nullable `SpaceId`
- Filter queries by `SpaceId IN (accessible spaces)`, never by `UserId`
- No foreign key from `AddedByUserId` / `CreatedByUserId` / `CheckedByUserId` to `User`; resolve display names via `ResolveActorName`, never a direct join
- Refresh tokens go to Key Vault; the database stores only `TokenSecretName`
- Never log tokens or full message contents
- Webhooks return `200 OK` immediately and enqueue for background processing; deduplicate on `ProviderMessageId`
- In background workers, set `CultureInfo.CurrentCulture` / `CurrentUICulture` from the user's `PreferredCulture` explicitly
- Notifications are structured domain events rendered per recipient, not pre-composed strings
- System prompts and tool descriptions stay in English, single version
- Never localize user content (item text, merchant, space name, event titles)
- Money is `decimal(18,2)`; parse amounts with the user's culture
- Telegram command names are canonical English; Italian aliases are router-only
- Dates and times: `DateTimeOffset` plus an IANA `TimeZoneId`, never a fixed offset
- UI colors and type come only from `wwwroot/css/tokens.css` (see `docs/12-stile-sito.md`); never hardcode a hex value in a component

## Style

- File-scoped namespaces, primary constructors, collection expressions (`= []`)
- Nullable reference types enabled, warnings as errors
- `async`/`await` with `Async` suffix and `CancellationToken` as the last parameter
- Domain events as `record`
- `AsNoTracking()` on read-only queries
- One type per file, file name matches the type
- Keep `Tessera.Core` free of EF, HTTP and Azure SDK references
