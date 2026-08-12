# 07 — Compliance, privacy, gestione dei token

> Questo documento raccoglie considerazioni tecniche e organizzative, **non è consulenza legale**. Per gli adempimenti GDPR formali — informativa, registro dei trattamenti, eventuale DPIA — vale la pena una consulenza professionale prima di aprire il servizio a utenti esterni alla propria cerchia.

## Il punto di partenza

Il prodotto custodisce refresh token che danno accesso al calendario e potenzialmente alla posta di altre persone. Questo cambia il livello di responsabilità rispetto a un'applicazione qualunque: come sviluppatore freelance che gestisce il servizio, si è **titolare del trattamento** per i dati degli utenti.

Non è bloccante — è lavoro da mettere in preventivo, e alcune scelte tecniche vanno prese all'inizio perché costose da recuperare.

## Gestione dei token OAuth

### Regola non negoziabile

**Nel database non entra mai un refresh token.** Solo il riferimento al segreto:

```csharp
public class LinkedAccount
{
    // ...
    public string TokenSecretName { get; set; } = null!;  // "oauth-google-{userId}"
    // NON: public string RefreshToken { get; set; }
}
```

Il token vive in **Azure Key Vault**, accesso via **Managed Identity** dell'App Service. Nessuna credenziale nel codice, nessun secret nel file di configurazione, nessun token in un dump del database.

### Perché anche in Key Vault

Un backup del database SQL, un log verboso, un'eccezione non gestita che serializza l'entità: sono tutte strade per cui un refresh token in colonna finisce dove non deve. Key Vault elimina la classe di problemi, non solo il caso specifico.

### Cache in memoria, con attenzione

Per non pagare una chiamata Key Vault per messaggio, i token si mettono in cache in memoria con TTL fino a `expiry − 5 minuti`. Vincoli:

- Solo `IMemoryCache`, **mai** su disco né in Redis non cifrato
- Cache invalidata al logout e alla revoca del collegamento
- I token non vanno mai loggati, nemmeno troncati (`token[..8]` in un log è già un'informazione da non conservare)

### Blazor server-side: la ragione principale

La console usa **Blazor Web App con render server interattivo**, non WASM. Motivo di sicurezza, non di preferenza: con WASM il codice gira nel browser e la gestione dei token diventa un problema (dove li tieni? come li rinnovi? come impedisci che finiscano in localStorage?).

Con il rendering server-side i token restano sul server, la sessione è un cookie `HttpOnly` `Secure` `SameSite=Lax`, e l'intera classe di problemi non esiste. Per una console di configurazione l'interattività richiesta è banale.

### Revoca

L'utente deve poter scollegare un account, e scollegare deve significare tre cose:

1. Chiamata all'endpoint di revoca del provider (Google: `https://oauth2.googleapis.com/revoke`)
2. Cancellazione del segreto da Key Vault
3. Cancellazione del record `LinkedAccount` e invalidazione della cache

Cancellare solo il record locale lascia un'autorizzazione attiva lato provider. È scorretto e l'utente lo vede nella propria pagina delle app autorizzate.

**Eccezione nota: Microsoft.** A differenza di Google, la piattaforma Microsoft identity non esporta un endpoint pubblico per revocare un singolo refresh token di un'app (l'unica API disponibile, `/me/revokeSignInSessions`, revoca *tutte* le sessioni dell'utente su *tutte* le app, non solo Tessera — uno scope troppo ampio per essere accettabile qui). Per Microsoft lo scollegamento esegue solo i passi 2 e 3: il segreto sparisce da Key Vault e il record locale viene cancellato, ma l'autorizzazione concessa a Tessera resta visibile e attiva lato Microsoft finché l'utente non la revoca lui stesso da [account.microsoft.com](https://account.microsoft.com) → Privacy → app con accesso ai propri dati (account personali), o tramite l'amministratore del tenant (account aziendali). Il refresh token stesso non è comunque più utilizzabile da Tessera una volta cancellato da Key Vault — il gap è solo sulla visibilità/revoca lato Microsoft, non su un rischio di accesso residuo da parte nostra.

## Scope: chiedere il minimo

Vale sia per la privacy che per l'approvazione della review.

| Bisogno | Scope corretto | Scope da evitare |
|---|---|---|
| Sapere quando l'utente è libero | `calendar.freebusy` | `calendar` |
| Mostrare gli eventi di domani | `calendar.readonly` | `calendar` |
| Creare e spostare eventi | `calendar.events` | `calendar` |

Il modello permessi con `AccessLevel.Availability` è progettato esattamente per questo: per rispondere a "quando siamo liberi giovedì?" non serve leggere i titoli degli eventi della moglie. `freebusy.query` restituisce solo le fasce occupate.

Questo è anche un buon argomento nella giustificazione degli scope durante la OAuth review: mostrare di aver scelto il permesso più stretto possibile aiuta l'approvazione.

## Google OAuth verification

### Classificazione

| Scope | Classe | Costo |
|---|---|---|
| Calendar (tutti) | **Sensitive** | Solo tempo: 2-6 settimane |
| Gmail (tutti) | **Restricted** | Security assessment CASA, migliaia di €/anno |

La differenza è la ragione per cui Gmail è in Fase 4 con un punto di domanda e Calendar è in Fase 2.

### Checklist

- [ ] Dominio verificato in Google Search Console
- [ ] Homepage pubblica sul dominio che descriva l'applicazione — la console assolve
- [ ] Privacy policy raggiungibile, con sezione **specifica** sull'uso dei dati Google
- [ ] Termini di servizio
- [ ] Video demo su YouTube che mostri: il consent screen, cosa l'utente autorizza, l'uso concreto di ogni scope richiesto
- [ ] Giustificazione scritta per ciascuno scope
- [ ] Dichiarazione di conformità alla Limited Use policy

Il video è la parte che si sottovaluta: deve mostrare il flusso reale, non delle slide.

### Il vincolo dello stato "Testing"

Finché l'app è in *Testing*:

- Massimo 100 utenti test
- **I refresh token scadono dopo 7 giorni**

Il secondo punto rende l'app inutilizzabile come prodotto: un assistente che chiede il re-login ogni settimana viene abbandonato. Da qui la regola operativa: **avviare la verification in Fase 1**, non alla fine.

### Limited Use policy

I dati Google possono essere usati solo per fornire la funzionalità all'utente. Non per addestrare modelli, non per profilazione, non per pubblicità. Ha una conseguenza tecnica concreta: se i dati di calendario passano attraverso Azure OpenAI, va verificato che il servizio non li usi per addestramento (Azure OpenAI non lo fa, e la cosa va documentata nella privacy policy).

## Microsoft

Molto più semplice: registrazione app su Entra ID + **publisher verification** tramite Partner Center. Giorni, non settimane.

Nota sui tenant aziendali: alcune organizzazioni richiedono il consenso dell'amministratore per app di terze parti. Va gestito come caso: se il consenso è negato, l'errore mostrato all'utente deve essere comprensibile, non un `AADSTS65001` grezzo.

## WhatsApp e Meta

- Business verification con documenti (P.IVA sufficiente per un freelance, tempi variabili)
- Privacy policy e termini richiesti in fase di configurazione
- I template dei messaggi vanno approvati singolarmente, con categoria dichiarata correttamente (dichiarare Utility ciò che è Marketing è una violazione con conseguenze sull'account)

## GDPR — sostanza

### Dati trattati

| Categoria | Dove | Base giuridica |
|---|---|---|
| Email, nome | Azure SQL | Esecuzione del contratto |
| Identità di canale (chat_id) | Azure SQL | Esecuzione del contratto |
| Lista della spesa, spese | Azure SQL | Esecuzione del contratto |
| Refresh token OAuth | Key Vault | Consenso esplicito |
| Eventi di calendario | **Non persistiti** — letti a runtime | Consenso |
| Testo dei messaggi | Transito verso Azure OpenAI | Esecuzione del contratto |

**Non persistere gli eventi di calendario** è una scelta importante: letti al momento, usati, scartati. Riduce drasticamente la superficie di rischio e semplifica la posizione GDPR. Se in futuro serve una cache per prestazioni, va limitata nel tempo, cifrata e dichiarata.

### Adempimenti minimi

- [ ] Informativa privacy chiara, non un copia-incolla generico
- [ ] **Informativa tradotta in ogni lingua dichiarata come supportata.** Per gli utenti UE un'informativa in una lingua non comprensibile è problematica, e la OAuth review di Google la esamina. Se una lingua non ha la traduzione, meglio non elencarla fra quelle supportate — vedi [09-localizzazione.md](09-localizzazione.md)
- [ ] Consenso separato e granulare per ciascuna integrazione
- [ ] Diritto di accesso: export dei propri dati dalla console (JSON, non serve elaborare)
- [ ] Diritto alla cancellazione: eliminazione account con revoca dei token e cancellazione dei dati
- [ ] **Pseudonimizzazione al posto della cancellazione a cascata** nei dati condivisi (vedi sotto)
- [ ] Registro dei trattamenti (documento interno)
- [ ] Data residency: risorse Azure in **West Europe** o **North Europe**, non fuori UE

### Il caso specifico: cancellazione di un account in spazi condivisi

C'è una tensione reale fra due esigenze entrambe legittime: il diritto alla cancellazione dell'interessato, e l'integrità dei dati degli **altri** membri di uno spazio condiviso. Le spese di gennaio dello spazio "Casa" non sono solo di chi le ha registrate: cancellarle altererebbe i totali storici del partner, che su quei dati ha un interesse proprio.

**Soluzione adottata: pseudonimizzazione, non cancellazione a cascata.**

Alla richiesta di cancellazione si rimuove tutto ciò che identifica la persona — account, email, nome, identità di canale, token OAuth (revocati anche presso il provider), calendari e mappature — e si conserva il solo GUID orfano sui riferimenti nelle risorse condivise. A quel punto il GUID non è più un dato personale: non è collegabile a una persona identificabile, perché ogni elemento identificativo è stato eliminato.

Il dettaglio tabellare di cosa si cancella e cosa resta è in [02-modello-dati.md](02-modello-dati.md).

Due conseguenze da gestire correttamente:

**Va dichiarato nell'informativa.** Che i contenuti inseriti in uno spazio condiviso restino visibili agli altri membri dopo la cancellazione dell'account non è ovvio per l'utente: va scritto chiaramente nell'informativa **prima** che qualcuno condivida uno spazio, non scoperto al momento della cancellazione.

**Il testo libero è l'eccezione.** Una nota su una spesa, il nome di una voce in lista, il testo di un promemoria possono contenere dati personali che la pseudonimizzazione del riferimento non tocca. Non esiste una regola automatica: la strada corretta è offrire in console, contestualmente alla cancellazione, l'eliminazione selettiva dei propri contenuti prima di chiudere l'account. Chi vuole solo andarsene lascia lo storico coerente; chi vuole cancellare tutto può farlo.

Nota che l'export dei dati (diritto di accesso) va prodotto **prima** della cancellazione e offerto nello stesso flusso: è il momento in cui l'utente ne ha bisogno.

### Il caso specifico: privacy mode del bot nei gruppi

Se si disattiva la privacy mode su Telegram, il bot legge **tutti** i messaggi del gruppo, non solo quelli diretti a lui. In un gruppo famigliare è un trattamento significativo che va dichiarato in modo esplicito nell'informativa.

Raccomandazione: **partire con privacy mode attiva** (solo comandi e menzioni). È meno comodo ma molto più difendibile. Se l'attrito si rivela eccessivo, la disattivazione va accompagnata da una comunicazione chiara e da un consenso raccolto nel gruppo, non solo da una riga nell'informativa.

### Il caso specifico: dati di terzi

Un evento di calendario contiene nomi ed email di persone che non sono utenti del servizio. Un'aggregazione delle disponibilità contiene informazioni su terzi. Non c'è modo di raccogliere il loro consenso.

È il motivo per cui `freebusy.query` è la scelta corretta: le fasce occupate senza titoli né partecipanti riducono al minimo il trattamento di dati di terzi. Va documentato come misura di minimizzazione.

## Sicurezza applicativa

| Area | Misura |
|---|---|
| Segreti | Key Vault + Managed Identity. Zero segreti nel repository, `dotnet user-secrets` in locale |
| Webhook | Validazione firma con confronto a tempo costante (`FixedTimeEquals`) |
| `LinkToken` | 32 byte da `RandomNumberGenerator`, TTL 10 min, monouso |
| Codice WhatsApp a 6 cifre | Max 5 tentativi, rate limit per numero, TTL 10 min |
| Autorizzazione | Filtro sistematico per spazi accessibili in **ogni** query, non solo controllo a valle |
| Trasporto | HTTPS obbligatorio, HSTS attivo |
| Cookie | `HttpOnly`, `Secure`, `SameSite=Lax` |
| Log | Mai token, mai contenuto integrale dei messaggi. Log strutturato con redazione |
| Rate limiting | Per `ChannelIdentity` sul webhook: un utente non deve poter generare costi LLM illimitati |
| Dipendenze | Dependabot attivo, `dotnet list package --vulnerable` in CI |

L'ultima riga sul rate limiting merita attenzione: senza un limite per utente, un loop accidentale o un utente in cattiva fede si traduce direttamente in fattura Azure OpenAI. Un limite tipo 60 messaggi/ora per identità è generoso per l'uso reale e protegge dal caso patologico.

## Nota sul rate limiting come protezione economica

Vale la pena implementarlo in Fase 1, non dopo. È dieci righe di codice con `RateLimiter` di .NET e chiude l'unico scenario in cui il costo del progetto può sfuggire di mano senza preavviso.

```csharp
builder.Services.AddRateLimiter(o => o.AddPolicy("per-identity", ctx =>
    RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers["X-Identity"].ToString(),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromHours(1) })));
```

Nel caso dei webhook la partizione va calcolata sull'identità estratta dal payload, non da un header: il rate limiting va quindi applicato nel `BackgroundService` o subito dopo la risoluzione dell'identità, non come middleware HTTP.
