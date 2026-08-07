# 11 — Logo

## Il segno

Quattro tessere leggermente ruotate — clay, ochre, teal, plum — come posate a mano. Pezzi separati che restano distinti e formano una forma unica solo stando insieme: è la tesi del prodotto reso in un'icona.

```
█▚  ▞█        clay   #B5502E
▚█  █▞        ochre  #C98A2E
              teal   #2E5F5A
              plum   #5B3A52
              ink    #221E1C  (grout, monocromia)
```

Nessun accento unico su fondo caldo: il colore viene dal mosaico stesso, coerente con il fatto che una tessera vera è sempre una di tante.

## File

```
brand/
├── favicon.ico              ← multi-risoluzione 16/32/48, per la console web
├── svg/
│   ├── tessera-mark.svg     ← segno pieno, per contesti che gestiscono già il proprio margine
│   ├── tessera-avatar.svg   ← margine di sicurezza integrato, per crop circolare
│   ├── tessera-mark-mono.svg
│   └── tessera-mark-white.svg
└── png/
    ├── tessera-mark-{512,256,128}.png
    ├── tessera-avatar-{512,256}.png
    ├── tessera-mark-mono-{512,128}.png
    ├── tessera-mark-white-{512,128}.png
    └── favicon-{48,32,16}.png
```

Gli SVG sono la fonte. I PNG sono derivati e vanno rigenerati da SVG se la palette cambia, non modificati a mano.

## Quale file per quale contesto

| Contesto | File | Perché |
|---|---|---|
| Avatar Telegram (`/setuserpic` su BotFather) | `tessera-avatar-512.png` | Margine integrato per il ritaglio circolare |
| Favicon console web | `favicon.ico` | Multi-risoluzione, letto nativamente dai browser |
| Icona PWA / manifest | `tessera-mark-512.png` | Piena, senza margine extra — il manifest applica il proprio |
| Header console, intestazioni email | `tessera-mark-128.png` o l'SVG diretto | Nitido a qualsiasi risoluzione dello schermo |
| Stampa B/N, timbri, watermark | `tessera-mark-mono` | Deve reggere senza il colore a fare il lavoro |
| Sfondo scuro o sfondo clay | `tessera-mark-white` | Il segno a colori piatti perde contrasto su fondi saturi |

**Il margine di sicurezza esiste per una ragione precisa.** Il segno pieno arriva a circa il 90% del raggio del riquadro: sufficiente per un ritaglio circolare standard, ma senza scorta. `tessera-avatar.svg` scala il segno all'82% prima di centrarlo, che è la versione da caricare ovunque il client applichi un ritaglio circolare non garantito — Telegram, WhatsApp, la maggior parte dei social. Il segno pieno resta per i contesti che controllano già il proprio margine (favicon, manifest).

## Verifica prima di ogni nuovo contesto

- **A 16 px il segno deve restare leggibile come quattro blocchi distinti**, non una macchia. Se un nuovo contesto (badge, notifica push) richiede una dimensione più piccola di 16 px, il segno pieno non regge: usare solo il colore clay come tinta unita, senza dettaglio.
- **Mai ricolorare le quattro tessere con un solo colore** fuori dalla monocromia dichiarata: perderebbe il senso del segno, che è "pezzi diversi, insieme".
- **Non ruotare, non specchiare, non aggiungere ombre.** La leggera rotazione delle tessere è già nel file sorgente; applicarne un'altra sopra rompe la composizione.

## Cosa manca, non urgente

- Wordmark (nome "Tessera" abbinato al segno) per intestazioni e footer — da fare quando serve un contesto con testo, non prima
- Icona adattiva Android (maschera separata da forma e sfondo) — solo se arriva un'app nativa, fuori scope attuale
- Verifica EUIPO del nome prima di investire ulteriore tempo di design — vedi [README.md](README.md)
