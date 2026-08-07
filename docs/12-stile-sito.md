# 12 — Stile del sito

Definisce i token visivi della console web e delle pagine pubbliche (homepage, privacy policy, termini), derivati dalla palette del logo ([11-logo.md](11-logo.md)) e non scelti indipendentemente. Se un componente nuovo richiede un colore o un font non elencato qui, la risposta di default è "non aggiungerlo", non "improvvisare qualcosa di simile".

## Perché non i due look che si vedono ovunque

Due combinazioni dominano il design generato da AI in questo periodo: sfondo crema caldo con accento terracotta unico e serif ad alto contrasto, oppure sfondo quasi nero con un solo accento acido. Il logo ha già risolto la domanda evitandole entrambe — il colore viene dal mosaico stesso, quattro tinte diverse, non un accento su un fondo neutro. Questo documento estende la stessa logica al resto del sito: **i colori del logo sono gli unici colori del sito**, usati con un significato funzionale, non decorativo.

## Palette

Stessi esadecimali del logo, nessuna variazione indipendente.

```css
:root {
  /* tessere — uso funzionale, non decorativo */
  --clay:   #B5502E;   /* brand, azioni primarie, focus */
  --ochre:  #C98A2E;   /* attenzione, avviso, permesso "Read" */
  --teal:   #2E5F5A;   /* successo, conferma, permesso "Write" */
  --plum:   #5B3A52;   /* informazione secondaria, permesso "Availability" */

  /* neutri */
  --ink:       #221E1C;   /* testo primario */
  --ink-soft:  #6B665D;   /* testo secondario, etichette */
  --paper:     #ECEAE3;   /* sfondo pagina */
  --paper-raised: #F7F5EF; /* card, superfici sollevate */
  --line:      #D7D3C6;   /* bordi, divisori */

  /* fuori dal set delle tessere, deliberatamente */
  --danger: #9B3B3B;   /* azioni distruttive — mai clay, per non confondersi col brand */
  --link:   #A24829;   /* clay scurito, solo per testo piccolo su paper: vedi contrasti */
}

[data-theme="dark"] {
  --ink:       #EDEAE2;
  --ink-soft:  #A8A29A;
  --paper:     #1B1B1C;
  --paper-raised: #242322;
  --line:      #3A3733;

  --clay:  #D9704A;
  --ochre: #E0A94C;
  --teal:  #4C8A85;
  --plum:  #86608A;
  --danger: #C25B5B;
  --link:  #E08A63;
}
```

**`--danger` non è una delle quattro tessere.** È la scelta deliberata più importante della palette: se "elimina spazio" avesse lo stesso colore di "azione primaria" (entrambi clay), un utente distratto rischierebbe di confermare una cancellazione pensando di confermare un salvataggio. Il rosso mattone sta fuori dal set apposta.

### Contrasti verificati

Calcolati (WCAG 2.1, non stimati) sulle combinazioni che il sito usa davvero:

| Combinazione | Rapporto | Testo normale | Elementi UI |
|---|---|---|---|
| ink su paper | 13.73 | ✅ AA | ✅ |
| ink-soft su paper | 4.74 | ✅ AA | ✅ |
| bianco su clay | 5.06 | ✅ AA | ✅ |
| bianco su teal | 7.24 | ✅ AA | ✅ |
| bianco su plum | 9.67 | ✅ AA | ✅ |
| ink su ochre | 5.63 | ✅ AA | ✅ |
| **bianco su ochre** | **2.93** | ❌ | ❌ |
| bianco su danger | 6.81 | ✅ AA | ✅ |
| **clay su paper** (testo) | **4.20** | ❌ (solo AA-large) | ✅ |
| `--link` (clay scurito) su paper | 4.99 | ✅ AA | ✅ |
| teal su paper | 6.01 | ✅ AA | ✅ |
| ink-dark su paper-dark | 14.32 | ✅ AA | ✅ |
| clay-dark su paper-dark | 5.22 | ✅ AA | ✅ |

Due conseguenze pratiche dalla tabella, non opzionali:

1. **I badge su fondo ochre usano testo ink, mai bianco.** Il bianco su ochre fallisce (2.93): è il colore più chiaro del set e va trattato come sfondo per testo scuro, non per testo chiaro.
2. **Il clay puro non è un colore da testo piccolo.** Come link inline su `paper` regge solo per titoli grandi (AA-large). Per link e testo piccolo esiste `--link`, la stessa tinta scurita del 10%: resta riconoscibile come clay ma supera 4.5:1. I bottoni non sono soggetti a questo vincolo perché il testo sopra è bianco, non clay su paper.

## Tipografia

| Ruolo | Font | Uso |
|---|---|---|
| Display | **Fraunces** (variabile) | Titoli, hero, wordmark. Serif calda con carattere — coerente col riferimento latino/mosaico del nome, non un serif neutro da editoriale |
| Corpo | **Inter** | Paragrafi, form, UI. Copertura eccellente di IT/EN, leggibile a ogni dimensione |
| Dati | **IBM Plex Mono** | Importi, date, ID, tabelle spese — la personalità da "registro contabile" è intenzionale, non un mono generico da codice |

```css
:root {
  --font-display: "Fraunces", "Iowan Old Style", Georgia, serif;
  --font-body:    "Inter", -apple-system, "Segoe UI", sans-serif;
  --font-mono:    "IBM Plex Mono", "SF Mono", Consolas, monospace;
}
```

**Fraunces va usato con moderazione**: titoli e la wordmark, mai il corpo del testo. È un font con personalità — un paragrafo intero in Fraunces stanca, un titolo in Fraunces si ricorda. Le varianti *optical size* e *soft* del font (se caricato come variabile) danno la leggera irregolarità che riprende l'idea delle tessere posate a mano; a pesi bassi restare su `wght 500-600`, evitare `wght 300` che perde carattere.

**IBM Plex Mono sugli importi non è un vezzo.** In una tabella di spese, cifre in un font proporzionale non si allineano in colonna e il confronto visivo "quanto ho speso rispetto al mese scorso" richiede uno sforzo in più. Un mono per i soli dati numerici risolve il problema e distingue visivamente "questo è un dato" da "questo è testo".

### Scala tipografica

Rapporto 1.25, base 16px:

```css
:root {
  --text-xs:   0.75rem;   /* 12px — etichette, badge */
  --text-sm:   0.875rem;  /* 14px — testo secondario */
  --text-base: 1rem;      /* 16px — corpo */
  --text-lg:   1.25rem;   /* 20px — sottotitoli */
  --text-xl:   1.563rem;  /* 25px — titoli di sezione */
  --text-2xl:  1.953rem;  /* 31px — titolo di pagina */
  --text-3xl:  2.441rem;  /* 39px — hero */
}
```

## Layout

### La homepage non apre con uno stat block

Il template generico per un prodotto SaaS è: titolo, sottotitolo, numero grande con etichetta piccola, accento a gradiente. Il soggetto di questo prodotto è una conversazione, non una metrica — quindi l'hero mostra quello: uno scambio reale nella chat, non una statistica.

```
+---------------------------------------+
|  [wordmark]                          |
|                                       |
|  Gestisci casa, spese e              |
|  calendario dalla chat.              |  <- Fraunces, text-3xl
|                                       |
|  +---------------------------+        |
|  | Tu: aggiungi il latte     |       |  <- mockup di chat reale,
|  | Tessera: OK Latte         |       |     non un'illustrazione
|  |          aggiunto         |       |     astratta
|  | Sara: quanto abbiamo      |       |
|  |       speso a gennaio?    |       |
|  | Tessera: 342EUR, di cui...|       |
|  +---------------------------+        |
|                                       |
|  [ Inizia su Telegram ]  <- clay     |
+---------------------------------------+
```

Il mockup di chat è il vero "eroe": mostra la tesi del prodotto (condivisione, più persone, più funzioni) senza doverla spiegare a parole.

### Griglia e spaziatura

Scala 4px, coerente fra console e pagine pubbliche:

```css
:root {
  --space-1: 0.25rem;  /* 4px */
  --space-2: 0.5rem;   /* 8px */
  --space-3: 0.75rem;  /* 12px */
  --space-4: 1rem;     /* 16px */
  --space-6: 1.5rem;   /* 24px */
  --space-8: 2rem;     /* 32px */
  --space-12: 3rem;    /* 48px */
  --space-16: 4rem;    /* 64px */

  --radius-sm: 4px;    /* input, badge */
  --radius-md: 8px;    /* card, bottoni */
  --radius-lg: 14px;   /* modali, superfici grandi */
}
```

I raggi degli angoli **non sono gli stessi delle tessere del logo** (che usano 6px su un riquadro di 34): sono scalati per la dimensione dei componenti reali. La coerenza è nel principio — angoli morbidi, mai squadrati, mai a pillola — non nel valore assoluto.

Console: contenuto max `72rem` (1152px), colonna singola sotto i 768px. Pagine pubbliche: max `48rem` (768px) per il testo, l'hero può eccedere per il mockup di chat.

## Il segno ricorrente

Il logo non è confinato all'angolo in alto a sinistra: il motivo delle tessere ricorre in tre punti del sito, con moderazione.

**1. Divisori di sezione.** Non una riga continua ma un piccolo gruppo di 2-3 quadratini colorati, eco delle tessere, al posto di un `<hr>` generico:

```html
<div class="seam" aria-hidden="true">
  <span style="background: var(--clay)"></span>
  <span style="background: var(--teal)"></span>
  <span style="background: var(--ochre)"></span>
</div>
```

**2. Indicatore di caricamento.** Invece di uno spinner circolare generico, le quattro tessere che si illuminano in sequenza — usa una forma già disegnata invece di introdurne una nuova, e nei momenti di attesa rinforza il marchio senza aggiungere altro contenuto.

**3. Badge di permesso, mappati sul modello dati.** I livelli di `AccessLevel` ([02-modello-dati.md](02-modello-dati.md)) hanno un colore fisso e coerente in ogni punto della console dove compaiono:

| `AccessLevel` | Colore badge | Testo |
|---|---|---|
| `None` | `--ink-soft`, contorno | ink-soft |
| `Availability` | `--plum` | bianco |
| `Read` | `--ochre` | **ink** (mai bianco — vedi contrasti) |
| `Write` | `--teal` | bianco |
| `Admin` | `--clay` | bianco |

Non è una scelta estetica isolata: è la struttura che porta informazione, come richiesto dal principio "structure is information". Un membro che scorre la pagina di uno spazio riconosce il proprio livello di accesso dal colore prima ancora di leggere la parola, e lo stesso codice colore vale identico nel bot quando descrive un errore di permesso (vedi [10-conversazione.md](10-conversazione.md)).

**Cosa non fare con il segno**: non trasformarlo in texture di sfondo, non ripeterlo come watermark decorativo dietro il testo, non usarlo come bordo di ogni card. La moderazione è il punto — se compare ovunque smette di significare qualcosa.

## L'imperfezione intenzionale — il rischio preso

Le tessere del logo sono leggermente ruotate, non allineate a una griglia perfetta: è un mosaico posato a mano, non stampato a macchina. Lo stesso principio si applica, con grande parsimonia, a tre punti del sito:

- Il piccolo divisore "seam" ha i quadratini con una rotazione di 1-2 gradi ciascuno, non identici
- L'illustrazione dell'hero (se presente oltre al mockup di chat) evita la simmetria perfetta
- Gli avatar generati per gli spazi senza foto (iniziale su sfondo colorato) usano un leggero offset invece di centratura matematica esatta

Questo è il rischio estetico dichiarato del documento: **niente nel sito deve sembrare perfettamente allineato al pixel come un template**. È un dettaglio che si nota per sottrazione — nessuno lo definirebbe a parole guardando il sito, ma è la differenza fra "sembra fatto da chiunque" e "sembra fatto apposta". Va applicato con disciplina: un solo elemento per pagina, mai sull'intera interfaccia, o l'effetto degrada in disordine percepito come bug.

## Componenti

### Bottoni

```css
.btn-primary   { background: var(--clay);  color: #fff; }
.btn-secondary { background: transparent;  color: var(--ink); border: 1px solid var(--line); }
.btn-success   { background: var(--teal);  color: #fff; }  /* confermare, salvare */
.btn-danger    { background: var(--danger); color: #fff; } /* elimina, rimuovi */
```

Un solo bottone primario per schermata. Se una pagina sembra averne bisogno di due, è la pagina ad avere due azioni concorrenti da separare, non un problema di stile.

### Card

Sfondo `--paper-raised`, bordo `--line` da 1px, **mai ombra pesante**: un'ombra minima (`0 1px 2px rgba(0,0,0,0.04)`) o nessuna, il bordo sottile basta a separare dal fondo. Le ombre morbide e diffuse sono la firma visiva più riconoscibile dei template generici.

### Stati vuoti e di errore

Coerenti con il tono definito per il bot in [10-conversazione.md](10-conversazione.md): un errore dice cosa è successo e come risolverlo, senza scuse; uno stato vuoto è un invito ad agire, non un'assenza da annunciare.

```
NO: "Si è verificato un errore. Ci scusiamo per il disagio."
SI: "Lo spazio non ha calendari collegati. Collega Google o Microsoft dalle impostazioni."

NO: "Nessuna spesa trovata."
SI: "Non ci sono ancora spese in questo spazio. Registra la prima dal bot o da qui."
```

## Movimento

Minimo e mai decorativo, coerente con [05-ottimizzazioni.md](05-ottimizzazioni.md) sul non aggiungere ciò che non serve.

```css
:root {
  --duration-fast: 120ms;
  --duration-base: 180ms;
  --ease: cubic-bezier(0.2, 0, 0, 1);
}

@media (prefers-reduced-motion: reduce) {
  * { transition-duration: 0.01ms !important; animation-duration: 0.01ms !important; }
}
```

Usato per: hover e focus dei componenti interattivi, comparsa dei toast di conferma, transizione di navigazione di Blazor (fade + leggero spostamento verticale, 180ms). Non usato per: contenuto che appare a cascata, effetti ambientali, parallasse. `prefers-reduced-motion` va rispettato senza eccezioni.

## Accessibilità

- Focus visibile sempre: anello da 2px in `--teal` (mai `outline: none` senza sostituto)
- Target minimo 44×44px per elementi toccabili, coerente con l'uso da telefono del bot e non solo della console desktop
- I badge di permesso portano anche testo, non solo colore (daltonismo)
- Dark mode segue `prefers-color-scheme` di default, con toggle manuale in console che sovrascrive

## Implementazione in Blazor

Un unico file di token globale, caricato una volta:

```
src/Tessera.Web/wwwroot/css/tokens.css     <- variabili di questo documento
src/Tessera.Web/wwwroot/css/base.css       <- reset, tipografia di base
src/Tessera.Web/Components/**/*.razor.css  <- CSS isolation per componente
```

`tokens.css` e `base.css` si importano una sola volta in `App.razor`. I singoli componenti usano CSS isolation di Blazor (`Componente.razor.css`) e leggono le variabili con `var(--clay)` — mai duplicare un valore esadecimale in un file di componente: se cambia la palette, deve bastare modificare `tokens.css`.

Le pagine pubbliche (homepage, privacy policy) condividono lo stesso `tokens.css`: non è un sito a parte con la sua identità, è la stessa applicazione con un layout diverso.

## Cosa manca, non urgente

- Wordmark completo (segno + nome in Fraunces) per intestazioni e footer — dipende da [11-logo.md](11-logo.md)
- Illustrazioni per gli stati vuoti oltre al testo — da fare quando il flusso reale rivela quali stati vuoti contano davvero
- Componenti data-table per le aggregazioni di spesa (fase 2-4) — il font mono e la palette sono già pronti, manca solo il layout tabellare
