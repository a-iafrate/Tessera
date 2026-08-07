# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

**Tessera** — a personal/family assistant bot for Telegram (phase 1) and WhatsApp (phase 3), plus a Blazor web console. Manages shared shopping lists, family expense tracking, reminders and calendars.

Core model: `User → Membership(per-resource permission) → Space → Resources`. Everything is shareable by construction; a personal resource is a Space with one member.

## Language policy — non-negotiable

| Artifact | Language |
|---|---|
| Code, identifiers, type names, file names | **English** |
| Comments and XML doc comments | **English** |
| Commit messages, branch names, PR titles | **English** |
| Log messages, exception messages | **English** |
| Test names and test data | **English** |
| Resource keys (`.resx` keys) | **English** |
| Default resource values (`Messages.resx`) | **English** |
| Italian user-facing strings | `Messages.it.resx` only |
| `docs/` | **Italian** — do not translate, do not rewrite in English |

Never hardcode user-facing text in any language: it goes through `IStringLocalizer` with an English key. A literal Italian string in code is a bug.

Conversation with the developer may be in Italian; the code produced must not be.

## Documentation map

Design decisions live in `docs/`. **Read the relevant file before implementing** — do not infer the design from code alone, and do not reload files already in context.

| File | Read when working on |
|---|---|
| `docs/README.md` | Project overview, product thesis |
| `docs/01-architettura.md` | Project layout, hosting, message pipeline, scheduler, `Program.cs` |
| `docs/02-modello-dati.md` | Any entity, migration, permission check, query — **the most important file** |
| `docs/03-integrazioni.md` | Telegram, WhatsApp, Google Calendar, Microsoft Graph |
| `docs/04-costi.md` | Anything with cost implications (model choice, notifications) |
| `docs/05-ottimizzazioni.md` | Intent router, prompt construction, caching, LLM calls |
| `docs/06-roadmap.md` | Scope questions — is this phase 1 or later? |
| `docs/07-compliance.md` | OAuth, tokens, GDPR, deletion, secrets |
| `docs/08-setup-sviluppo.md` | Build, CI, migrations, local webhook setup |
| `docs/09-localizzazione.md` | Anything user-facing, culture, formatting, notifications |
| `docs/10-conversazione.md` | Bot replies, onboarding, undo, error handling, tone |
| `docs/11-logo.md` | Which brand asset to use in which context |
| `docs/12-stile-sito.md` | Design tokens for the web console: colors, type, components |

## Stack

.NET 10 (LTS) · ASP.NET Core · Blazor Web App (interactive **server** render) · EF Core / Azure SQL · Azure App Service · Key Vault + Managed Identity · Azure OpenAI (gpt-4o-mini) · `Telegram.Bot` · `Microsoft.Graph` · `Google.Apis.Calendar.v3`

Single host: web console and bot webhooks live in the same ASP.NET application. See `docs/01-architettura.md`.

## Hard rules

Violating any of these is a defect, not a style preference. Rationale is in the referenced docs.

1. **No resource without `SpaceId`.** Not nullable, no "if personal" branch. → `02`
2. **Never query resources by `UserId`.** Always `SpaceId IN (accessible spaces)`. → `02`
3. **No foreign key from `AddedByUserId` / `CreatedByUserId` / `CheckedByUserId` to `User`.** Orphan GUIDs are intentional (account deletion). Always resolve names via `ResolveActorName`, never a direct join. → `02`
4. **Never store refresh tokens in the database.** Key Vault only; the DB holds `TokenSecretName`. → `07`
5. **Never log tokens**, not even truncated. → `07`
6. **Webhooks return `200 OK` immediately**, then process in background. Deduplicate on `ProviderMessageId`. → `01`
7. **Set the user's culture explicitly in the background worker.** No HTTP context there; omitting it silently falls back to English. → `09`
8. **Notifications are structured domain events rendered per recipient**, never pre-composed strings. → `09`
9. **System prompt and tool descriptions stay in English**, in one version. Translating them breaks prompt caching. → `05` `09`
10. **Never localize user content** (item text, merchant, space name, event titles). Only UI text. → `09`
11. **Money is `decimal(18,2)`**, never `double`. Parse with the user's culture. → `02` `09`
12. **Telegram command names are canonical English**; Italian aliases are accepted by the router but not listed in the menu. → `03` `09`
13. **Intent router: fast path only for `it` and `en`.** Other cultures always go to the LLM. → `05` `09`
14. **Date parsing goes to the LLM**, and the interpreted result is read back in the confirmation. → `05`
15. **Effective calendar level is `min(ProviderRole, ShareLevel, MembershipPermission)`**, computed in one place. → `02`

## Conventions

- File-scoped namespaces, primary constructors, collection expressions (`= []`)
- Nullable reference types enabled; treat warnings as errors
- `async`/`await` throughout, `Async` suffix, `CancellationToken` as last parameter
- Domain events as `record`
- `AsNoTracking()` on read-only queries
- `Tessera.Core` has no infrastructure dependencies — no EF, no HTTP, no Azure SDK
- Authorization logic lives in `Tessera.Core` and is unit-tested without a database
- One class per file, file name matches the type

## Commands

```bash
dotnet build
dotnet test
dotnet run --project src/Tessera.Web

dotnet ef migrations add <Name> --project src/Tessera.Data --startup-project src/Tessera.Web
dotnet ef database update      --project src/Tessera.Data --startup-project src/Tessera.Web
```

Never call `Database.Migrate()` at application startup — migrations run as a CI/CD step. → `08`

Secrets in development: `dotnet user-secrets`, never `appsettings.json`.

## Testing

The intent router tests are the most valuable in the codebase and regress easily. Every phrase that reaches the LLM fallback when it should have been handled deterministically gets added to the corpus. Corpus format and examples: `docs/08-setup-sviluppo.md`.

Permission checks (`IAccessPolicy`) require unit tests: a mistake there leaks data between members of a space.

## When unsure

- **Scope** ("should this exist yet?") → `docs/06-roadmap.md`. Phase 4 features are explicitly deferred; some are declared non-goals (expense splitting, meal planning, Alexa sync).
- **A design decision seems wrong** → say so before implementing around it. Several rules above are deliberate trade-offs with rationale in the docs.
- **A doc contradicts the code** → the docs are the intended design; flag the divergence rather than silently following either.
