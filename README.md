# Tessera — Bot personale su Telegram e WhatsApp

> **Bot Telegram:** [@tesseraapp_bot](https://t.me/tesseraapp_bot) — nome visualizzato "Tessera"

Il nome viene dal *tessera* latino: il singolo tassello di un mosaico. È la tesi del prodotto — lista, spese, promemoria e calendario sono pezzi separati che formano un quadro unico solo stando nello stesso spazio condiviso.

**Da verificare prima di registrare il dominio:** ricerca di anteriorità EUIPO in classe 9 e 42. Esistono usi noti del nome in ambito tecnologico (Tessera Technologies, poi Xperi/Adeia, nei semiconduttori; Tessera Therapeutics nel biotech) — nessuno nel software di consumo, ma la verifica va fatta.

Assistente conversazionale per uso personale e familiare, accessibile via Telegram (fase 1) e WhatsApp (fase 3), con una console web per registrazione e configurazione.

## Cosa fa

| Funzione | Fase | Richiede OAuth |
|---|---|---|
| Lista della spesa condivisa | 1 | No |
| Tracciamento spese familiari e aggregazioni | 1 | No |
| Promemoria condivisi, anche ricorrenti | 1 | No |
| Spese ricorrenti e budget con avviso alla soglia | 1 | No |
| Digest quotidiano | 1 | No |
| Console web di configurazione | 1 | No |
| Calendario: lettura eventi, disponibilità incrociate | 2 | Sì (Google, Microsoft) |
| Canale WhatsApp | 3 | — |
| Scontrini via foto (vision), storico prezzi, archivio garanzie | 4 | No |
| Email | 4 | Sì ⚠️ costo di audit elevato |

La condivisione è il cuore del prodotto: uno **spazio** raggruppa più utenti e più risorse, con permessi granulari per tipo di risorsa. Lista della spesa condivisa con il partner, sola disponibilità del calendario con gli amici.

La risorsa personale è il caso degenere — uno spazio con un solo membro. **Tutto è condivisibile per costruzione**, non per estensione successiva: vedi [02-modello-dati.md](02-modello-dati.md).

Interfaccia e interazione sono **multilingua** (italiano ed inglese con supporto completo, altre lingue via LLM): vedi [09-localizzazione.md](09-localizzazione.md).

## Stack

.NET 10 · ASP.NET Core · Blazor Web App (server interactive) · EF Core · Azure App Service (Windows o Linux) · Azure SQL o Cosmos DB · Key Vault · Azure OpenAI (gpt-4o-mini) · Telegram.Bot · Microsoft Graph SDK · Google.Apis.Calendar.v3

Host singolo: la console web e i webhook dei bot vivono nella stessa applicazione ASP.NET. Vedi [01-architettura.md](01-architettura.md).

## Convenzioni linguistiche

| Artefatto | Lingua |
|---|---|
| Codice, identificatori, nomi di file | **Inglese** |
| Commenti e XML doc | **Inglese** |
| Messaggi di commit, branch, PR | **Inglese** |
| Log e messaggi di eccezione | **Inglese** |
| Chiavi delle risorse e `Messages.resx` | **Inglese** |
| Testi in italiano per l'utente | solo `Messages.it.resx` |
| Questa documentazione (`docs/`) | **Italiano** |

Nessun testo destinato all'utente va scritto direttamente nel codice: passa da `IStringLocalizer` con una chiave inglese. Vedi [09-localizzazione.md](09-localizzazione.md).

Le istruzioni per gli assistenti AI sono versionate nel repository: `CLAUDE.md` alla root e `.github/copilot-instructions.md`. Contengono la sintesi delle regole e rimandano a questa cartella per il razionale — motivo per cui `docs/` deve stare nel repository e non in un wiki esterno. Dettagli in [08-setup-sviluppo.md](08-setup-sviluppo.md).

## Documentazione

| Documento | Contenuto |
|---|---|
| [01-architettura.md](01-architettura.md) | Struttura della soluzione, host unico, pipeline dei messaggi, deploy Azure |
| [02-modello-dati.md](02-modello-dati.md) | Spazi, membership, permessi, risorse polimorfiche, schema EF Core |
| [03-integrazioni.md](03-integrazioni.md) | Telegram, WhatsApp Cloud API, Google Calendar, Microsoft Graph, Alexa (scartata) |
| [04-costi.md](04-costi.md) | Stima mensile per infrastruttura e LLM, scenari di crescita |
| [05-ottimizzazioni.md](05-ottimizzazioni.md) | Router di intent, prompt caching, riduzione latenza e token |
| [06-roadmap.md](06-roadmap.md) | Fasi, stime, punti di decisione |
| [07-compliance.md](07-compliance.md) | OAuth verification, GDPR, gestione dei token |
| [08-setup-sviluppo.md](08-setup-sviluppo.md) | Ambiente locale, ngrok, secrets, migrations |
| [09-localizzazione.md](09-localizzazione.md) | Multilingua: router per lingua, comandi, notifiche per destinatario |
| [10-conversazione.md](10-conversazione.md) | Onboarding, undo, recupero dagli errori, tono dei messaggi |
| [11-logo.md](11-logo.md) | Il segno, i file, quale usare per ogni contesto |
| [12-stile-sito.md](12-stile-sito.md) | Palette, tipografia, componenti — coerenti col logo |

## Quick start (sviluppo)

```bash
git clone <repo> && cd tessera
dotnet restore
dotnet user-secrets set "Telegram:BotToken" "<token>" --project src/Tessera.Web
dotnet ef database update --project src/Tessera.Data --startup-project src/Tessera.Web
dotnet run --project src/Tessera.Web
```

In un secondo terminale, per esporre il webhook:

```bash
ngrok http https://localhost:7001
# poi registra il webhook — vedi 08-setup-sviluppo.md
```

## Principio guida

Il rischio del progetto non è tecnico. Le API sono documentate, lo stack è noto, l'infrastruttura costa poche decine di euro al mese. Il rischio è che nessuno usi un bot per la lista della spesa quando esiste già l'app delle note.

**La tesi del prodotto non è nessuna singola funzione.** Feature per feature si perde contro ogni incumbent: Bring! sulle liste, Splitwise sulle spese, Google Calendar sul calendario. La tesi è che liste, spese, promemoria e calendario vivano nello **stesso spazio condiviso, dentro la chat dove già si parla con la propria famiglia** — e che questo renda possibili cose che nessun pezzo singolo può fare (lo scontrino che spunta la lista e registra la spesa, la cena in calendario che propone gli acquisti).

Corollario operativo: ogni funzione oltre la Fase 1 deve giustificarsi come **connessione fra due esistenti**, non come voce autonoma in elenco. La tesi della suite rende razionale ogni aggiunta, ed è il meccanismo con cui i progetti non arrivano in produzione.

Per questo la Fase 1 è deliberatamente minima e senza OAuth: serve a scoprire la retention prima di spendere settimane in verification e business account. Il punto di decisione è alla fine della Fase 1, non alla fine del progetto.

E per questo [10-conversazione.md](10-conversazione.md) pesa quanto i documenti sulle funzioni: la qualità dell'onboarding e del recupero dagli errori determina la retention più di qualsiasi capacità aggiuntiva.
