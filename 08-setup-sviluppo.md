# 08 — Setup ambiente di sviluppo

## Prerequisiti

- .NET 10 SDK
- SQL Server LocalDB, o container `mcr.microsoft.com/mssql/server:2022-latest`
- ngrok (o Dev Tunnels di Visual Studio) per esporre il webhook
- Azure CLI per Key Vault e deploy

Con più progetti .NET sulla stessa macchina, conviene fissare la versione dell'SDK per la solution:

```json
// global.json nella root
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

## Creazione della solution

```bash
mkdir tessera && cd tessera
dotnet new sln -n Tessera

dotnet new blazor       -n Tessera.Web          -o src/Tessera.Web --interactivity Server
dotnet new classlib     -n Tessera.Core         -o src/Tessera.Core
dotnet new classlib     -n Tessera.Channels     -o src/Tessera.Channels
dotnet new classlib     -n Tessera.Integrations -o src/Tessera.Integrations
dotnet new classlib     -n Tessera.Ai           -o src/Tessera.Ai
dotnet new classlib     -n Tessera.Data         -o src/Tessera.Data
dotnet new xunit        -n Tessera.Core.Tests   -o tests/Tessera.Core.Tests
dotnet new xunit        -n Tessera.Ai.Tests     -o tests/Tessera.Ai.Tests

dotnet sln add (ls -r **/*.csproj)   # PowerShell; su bash: find . -name "*.csproj" -exec dotnet sln add {} \;
```

Riferimenti — `Tessera.Core` non referenzia nulla:

```bash
cd src
dotnet add Tessera.Data/Tessera.Data.csproj                 reference Tessera.Core/Tessera.Core.csproj
dotnet add Tessera.Channels/Tessera.Channels.csproj         reference Tessera.Core/Tessera.Core.csproj
dotnet add Tessera.Integrations/Tessera.Integrations.csproj reference Tessera.Core/Tessera.Core.csproj
dotnet add Tessera.Ai/Tessera.Ai.csproj                     reference Tessera.Core/Tessera.Core.csproj
dotnet add Tessera.Web/Tessera.Web.csproj                   reference Tessera.Core/Tessera.Core.csproj
dotnet add Tessera.Web/Tessera.Web.csproj                   reference Tessera.Data/Tessera.Data.csproj
dotnet add Tessera.Web/Tessera.Web.csproj                   reference Tessera.Channels/Tessera.Channels.csproj
dotnet add Tessera.Web/Tessera.Web.csproj                   reference Tessera.Integrations/Tessera.Integrations.csproj
dotnet add Tessera.Web/Tessera.Web.csproj                   reference Tessera.Ai/Tessera.Ai.csproj
```

## Pacchetti principali

```bash
# Web
dotnet add src/Tessera.Web package Microsoft.AspNetCore.Identity.EntityFrameworkCore
dotnet add src/Tessera.Web package Azure.Identity
dotnet add src/Tessera.Web package Azure.Extensions.AspNetCore.Configuration.Secrets
dotnet add src/Tessera.Web package Microsoft.ApplicationInsights.AspNetCore

# Data
dotnet add src/Tessera.Data package Microsoft.EntityFrameworkCore.SqlServer
dotnet add src/Tessera.Data package Microsoft.EntityFrameworkCore.Design

# Canali
dotnet add src/Tessera.Channels package Telegram.Bot

# Integrazioni
dotnet add src/Tessera.Integrations package Microsoft.Graph
dotnet add src/Tessera.Integrations package Microsoft.Identity.Web
dotnet add src/Tessera.Integrations package Google.Apis.Calendar.v3

# AI
dotnet add src/Tessera.Ai package Azure.AI.OpenAI
dotnet add src/Tessera.Ai package Microsoft.SemanticKernel   # opzionale, valutare se serve
```

Nota su Semantic Kernel: utile per l'orchestrazione con function calling, ma aggiunge astrazione. Con pochi tool, `Azure.AI.OpenAI` diretto è più prevedibile e più facile da debuggare. Valutarlo dopo aver visto quanti tool servono davvero.

## Secrets in locale

Mai nel repository. `dotnet user-secrets`:

```bash
cd src/Tessera.Web
dotnet user-secrets init

dotnet user-secrets set "Telegram:BotToken"      "123456:ABC-DEF..."
dotnet user-secrets set "Telegram:WebhookSecret" "$(openssl rand -hex 32)"
dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://....openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey"     "..."
dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o-mini"
dotnet user-secrets set "ConnectionStrings:Default" \
  "Server=(localdb)\\MSSQLLocalDB;Database=Tessera;Trusted_Connection=True;"
```

In produzione le stesse chiavi arrivano da Key Vault via Managed Identity. Configurazione in `Program.cs`:

```csharp
if (!builder.Environment.IsDevelopment())
{
    builder.Configuration.AddAzureKeyVault(
        new Uri($"https://{builder.Configuration["KeyVault:Name"]}.vault.azure.net/"),
        new DefaultAzureCredential());
}
```

`DefaultAzureCredential` usa la Managed Identity in Azure e le credenziali della Azure CLI in locale: lo stesso codice funziona in entrambi i contesti.

## Bot Telegram di sviluppo

Creare **due bot** su [@BotFather](https://t.me/BotFather): uno `_dev` e uno di produzione. Condividere il token fra ambienti significa che un `setWebhook` in locale ruba i messaggi alla produzione — Telegram ammette un solo webhook per bot.

```
/newbot
→ nome: Tessera Dev
→ username: tesseraapp_dev_bot
```

Impostazioni utili:

```
/setcommands       → registra /list, /expense, /month, /link, /language, /help (nomi canonici EN, vedi 09)
/setprivacy        → Enable (raccomandato, vedi 07-compliance.md)
/setjoingroups     → Enable
/setdescription    → testo mostrato prima di /start
```

## Esporre il webhook

```bash
ngrok http https://localhost:7001
# → Forwarding  https://abc123.ngrok-free.app -> https://localhost:7001
```

Registrare il webhook:

```bash
BOT_TOKEN="..."
SECRET="..."
URL="https://abc123.ngrok-free.app/hooks/telegram"

curl -X POST "https://api.telegram.org/bot$BOT_TOKEN/setWebhook" \
  -H "Content-Type: application/json" \
  -d "{\"url\":\"$URL\",\"secret_token\":\"$SECRET\",\"allowed_updates\":[\"message\",\"callback_query\",\"my_chat_member\"]}"
```

**`my_chat_member` è obbligatorio**, e si dimentica facilmente: senza, il bot non riceve gli eventi di aggiunta e rimozione da un gruppo. Nota che `allowed_updates` funziona come whitelist esclusiva — omettere un tipo significa non riceverlo mai, senza alcun errore. Se in futuro servono altri tipi (`edited_message`, `chat_member`), vanno aggiunti qui, non altrove.

Verifica e diagnostica:

```bash
curl "https://api.telegram.org/bot$BOT_TOKEN/getWebhookInfo"
```

`last_error_message` in questa risposta è lo strumento di debug più utile quando "il bot non risponde": mostra l'errore che Telegram ha ricevuto dal tuo endpoint.

Rimozione:

```bash
curl "https://api.telegram.org/bot$BOT_TOKEN/deleteWebhook"
```

Con l'URL ngrok che cambia a ogni riavvio, conviene uno script `dev-webhook.ps1` che legga l'URL dall'API locale di ngrok (`http://127.0.0.1:4040/api/tunnels`) e faccia il `setWebhook` automaticamente.

**Alternativa**: in sviluppo si può usare il **long polling** (`StartReceiving` di `Telegram.Bot`) invece del webhook, evitando ngrok del tutto. Richiede di astrarre la fonte dei messaggi, ma rende il ciclo di sviluppo molto più fluido. Consigliato.

## Migrations

```bash
dotnet ef migrations add InitialCreate \
  --project src/Tessera.Data --startup-project src/Tessera.Web

dotnet ef database update \
  --project src/Tessera.Data --startup-project src/Tessera.Web
```

In produzione: **non** applicare le migration all'avvio dell'app. Con `Database.Migrate()` in `Program.cs` un deploy fallito a metà lascia lo schema in stato incerto, e con più istanze si generano corse. Applicarle come step della pipeline CI/CD, prima dello swap.

## GitHub Actions

```yaml
name: deploy
on:
  push:
    branches: [main]

jobs:
  build-deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release
      - run: dotnet list package --vulnerable --include-transitive

      - run: dotnet publish src/Tessera.Web -c Release -o ./publish

      - uses: azure/login@v2
        with:
          creds: ${{ secrets.AZURE_CREDENTIALS }}

      # migration prima del deploy
      - run: |
          dotnet tool install -g dotnet-ef
          dotnet ef database update \
            --project src/Tessera.Data --startup-project src/Tessera.Web \
            --connection "${{ secrets.SQL_CONNECTION }}"

      - uses: azure/webapps-deploy@v3
        with:
          app-name: tessera-web
          package: ./publish
```

Consigliato l'uso di **OIDC federated credentials** al posto di `AZURE_CREDENTIALS` con secret: elimina un segreto di lunga durata dal repository.

## Configurazione della Web App

Da fare una volta:

```bash
RG=rg-tessera
APP=tessera-web

az webapp config set -g $RG -n $APP --always-on true
az webapp config set -g $RG -n $APP --http20-enabled true
az webapp update  -g $RG -n $APP --https-only true

# Managed Identity + Key Vault
az webapp identity assign -g $RG -n $APP
PRINCIPAL=$(az webapp identity show -g $RG -n $APP --query principalId -o tsv)
az keyvault set-policy -n kv-tessera --object-id $PRINCIPAL --secret-permissions get list set delete
```

`--always-on true` non è opzionale: senza, l'app va in idle e il primo webhook dopo l'inattività va in timeout.

## Test del router

I test più importanti della codebase. Corpus in un file separato, che cresce osservando i log di produzione.

```csharp
public class IntentRouterTests
{
    public static TheoryData<string, string, string?> Corpus => new()
    {
        // italiano — gestiti a L2, senza LLM
        { "it", "aggiungi il latte",                     "shopping.add" },
        { "it", "metti 2 litri di latte nella lista",     "shopping.add" },
        { "it", "segna pane",                             "shopping.add" },
        { "it", "cosa c'è in lista",                      "shopping.show" },
        { "it", "quanto ho speso a gennaio",              "expenses.query" },

        // italiano — devono cadere a L3
        { "it", "ricordati che serve il pane",            null },
        { "it", "finito il detersivo",                    null },
        { "it", "sposta la riunione con Marco e avvisalo", null },

        // inglese — L2
        { "en", "add milk",                               "shopping.add" },
        { "en", "put bread on the list",                  "shopping.add" },
        { "en", "how much did I spend in January",         "expenses.query" },

        // inglese — L3
        { "en", "we're out of detergent",                 null },

        // lingua senza matcher — sempre L3, per progetto
        { "de", "milch hinzufügen",                        null },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Classifica_correttamente(string culture, string input, string? expectedIntent)
    {
        var match = new IntentRouter(Matchers.All).TryRoute(input, culture);
        Assert.Equal(expectedIntent, match?.Intent);
    }
}
```

Regola operativa: **ogni frase che in produzione finisce a L3 quando poteva essere gestita a L2 va aggiunta al corpus.** Il router migliora osservando i log, non ragionando a tavolino.

## Debug: i problemi che si presentano per primi

| Sintomo | Causa probabile |
|---|---|
| Il bot non risponde | `getWebhookInfo` → `last_error_message`. Spesso certificato ngrok o 500 nell'endpoint |
| Messaggi duplicati | Il webhook non risponde `200 OK` abbastanza in fretta, oppure manca la deduplica su `ProviderMessageId` |
| Il bot non vede i messaggi nel gruppo | Privacy mode attiva: solo comandi e menzioni. È il comportamento corretto |
| Il bot ha smesso di rispondere in un gruppo che prima funzionava | Il gruppo è diventato supergruppo e il `chat_id` è cambiato: `Space.GroupChatId` punta nel vuoto. Vedi [03](03-integrazioni.md) |
| Notifica ricevuta due volte per la stessa azione | Notificato sia nel gruppo che in privato: manca la soppressione per canale di origine |
| Nome vuoto o GUID al posto dell'autore di una spesa | Resa che fa join su `User` invece di passare da `ResolveActorName`: non c'è FK, il riferimento può essere orfano per progetto |
| Cancellazione account fallita per violazione di vincolo | È stata aggiunta una FK da `AddedByUserId` verso `User`: va rimossa, vedi [02](02-modello-dati.md) |
| Primo messaggio del giorno lentissimo | SQL serverless in auto-pause, oppure always-on disattivato |
| Refresh token Google scaduto dopo 7 giorni | L'app OAuth è ancora in stato *Testing*. Vedi [07-compliance.md](07-compliance.md) |
| `401` sul webhook in locale | Secret token diverso fra `setWebhook` e user-secrets |
| Orari sbagliati di un'ora | Timezone gestita con offset fisso invece di IANA ID. Compare al cambio d'ora |
| Tutti i messaggi in inglese pur avendo l'italiano impostato | Cultura non impostata nel `BackgroundService`: non c'è HTTP context, nessun middleware l'ha fatto. Vedi [09](09-localizzazione.md) |
| `new CultureInfo("it")` non ha effetto, formati sempre invariant | `InvariantGlobalization` è `true`: l'app non ha dati di cultura. Va messo a `false` in `Directory.Build.props` |
| Importo registrato 100 volte più grande | Parsing con `InvariantCulture` invece della cultura dell'utente: `"12,50"` letto come 1250 |
| Notifica arrivata nella lingua del mittente | Notifica composta come stringa e inoltrata, invece di evento reso per destinatario |
| Un articolo della lista appare tradotto | Il contenuto utente non va mai localizzato — solo l'interfaccia |

## Impostazioni comuni di compilazione

`Directory.Build.props` nella root, così il target framework e le regole valgono per tutti i progetti senza ripeterli:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <InvariantGlobalization>false</InvariantGlobalization>
    <NeutralLanguage>en</NeutralLanguage>
  </PropertyGroup>
</Project>
```

Due proprietà meritano una nota:

`InvariantGlobalization` deve restare **false**. Con `true` l'applicazione non ha dati di cultura: `new CultureInfo("it")` degrada silenziosamente a invariant, e il parsing degli importi e la formattazione delle date smettono di funzionare come previsto. È abilitato per default in alcuni template container ed è il tipo di impostazione che rompe la localizzazione senza un errore evidente. Vedi [09-localizzazione.md](09-localizzazione.md).

`NeutralLanguage = en` dichiara che `Messages.resx` senza suffisso contiene l'inglese, coerentemente con la policy per cui il codice e le risorse di riferimento sono in inglese.

Un `.editorconfig` alla root completa il quadro: `dotnet new editorconfig` produce una base ragionevole, a cui vale la pena aggiungere `csharp_style_namespace_declarations = file_scoped:warning` e `dotnet_diagnostic.CA1305.severity = warning` (che segnala le chiamate di formattazione senza `IFormatProvider` esplicito — esattamente la classe di bug descritta nel doc 09).

## Istruzioni per gli assistenti AI

Il progetto si sviluppa in Visual Studio con Copilot ed eventualmente Claude Code. Entrambi leggono file di istruzioni dal repository, che vanno versionati come il resto del codice.

| File | Letto da | Contenuto |
|---|---|---|
| `CLAUDE.md` (root) | Claude Code | Policy linguistica, mappa di `docs/`, regole non negoziabili, comandi |
| `.github/copilot-instructions.md` | GitHub Copilot (VS, VS Code, github.com) | Stesse regole in forma più compatta |

**Perché due file**: i due strumenti leggono percorsi diversi e non condividono convenzione. Il contenuto è volutamente ridondante ma non identico — `CLAUDE.md` include la mappa dei documenti e i comandi, perché Claude Code può leggere file su richiesta; le istruzioni Copilot restano più brevi perché vengono iniettate in ogni richiesta di completamento.

**Il punto chiave di entrambi è il rimando a `docs/`.** Le regole di progetto sono numerose e ripeterle per esteso in ogni prompt consumerebbe contesto inutilmente. I file di istruzioni contengono la sintesi e la tabella di quale documento leggere per quale attività; il razionale resta nei documenti, letti solo quando serve.

Conseguenza operativa: **`docs/` va nel repository**, non in un wiki esterno. Se i documenti non sono accessibili nel checkout, il rimando non funziona.

Nota sulla policy linguistica: entrambi i file dichiarano che codice, commenti, nomi e messaggi di commit sono in **inglese**, mentre `docs/` è in italiano e non va tradotto. Senza questa dichiarazione esplicita, un assistente che legge documentazione italiana tende a produrre identificatori e commenti in italiano.

Se in futuro servono istruzioni specifiche per aree del codice, Visual Studio supporta anche `.github/instructions/*.instructions.md` con un campo `applyTo` che le limita a certi percorsi (per esempio regole di test solo su `tests/**`). Utile ma non necessario all'inizio.

## Struttura del repository

```
tessera/
├── .github/
│   ├── workflows/deploy.yml
│   └── copilot-instructions.md   ← istruzioni per Copilot
├── docs/                         ← questa documentazione (in italiano)
├── src/
├── tests/
├── infra/                        ← Bicep, opzionale ma consigliato
├── CLAUDE.md                     ← istruzioni per Claude Code
├── Directory.Build.props
├── global.json
├── .editorconfig
├── Tessera.sln
├── .gitignore
└── README.md
```

`infra/` con un Bicep minimo (Web App, SQL, Key Vault, App Insights) vale il tempo speso: permette di ricreare l'ambiente da zero e di avere un secondo ambiente di staging senza rifare la configurazione a mano dal portale.
