# Protocollo PadScreen (v1)

Il PC si collega all'iPad: l'app iPad **ascolta** sulla porta TCP `47800` (tutte le interfacce)
e si annuncia via Bonjour come `_padscreen._tcp`.

- **Wi-Fi**: il PC apre una connessione TCP a `ip-iPad:47800`.
- **USB**: il PC chiede a `usbmuxd` (su Windows: *Apple Mobile Device Service*, `127.0.0.1:27015`)
  un tunnel verso la porta `47800` dell'iPad. Sull'iPad la connessione arriva da `127.0.0.1`.

## Framing

Ogni messaggio: `[tipo: u8][lunghezza payload: u32 big-endian][payload]`. Massimo 32 MiB.

| Tipo | Nome | Direzione | Payload |
|------|------|-----------|---------|
| 0x01 | HELLO | iPad → PC | JSON `{proto,name,model,width,height,scale,maxFps,appVersion}` (pixel nell'orientamento attuale) |
| 0x02 | SIZE | iPad → PC | come HELLO, inviato quando l'iPad ruota |
| 0x05 | PING | entrambe | 8 byte opachi; chi riceve risponde con PONG e lo stesso payload |
| 0x06 | PONG | entrambe | eco del PING |
| 0x07 | BYE | entrambe | vuoto |
| 0x10 | TOUCH | iPad → PC | `u8 n` + n × 26 byte: `u32 id, u8 fase, u8 tipo, f32 x, f32 y, f32 pressione, f32 altitudine, f32 azimut` |
| 0x11 | SCROLL | iPad → PC | `f32 dx, f32 dy` in punti (trackpad/mouse dell'iPad, scorrimento naturale) |
| 0x20 | KEYFRAME | iPad → PC | vuoto: il decoder chiede un nuovo fotogramma chiave |
| 0x81 | CONFIG | PC → iPad | JSON `{width,height,fps,encoder,host}` |
| 0x82 | VIDEO_HEADER | PC → iPad | `AVCDecoderConfigurationRecord` (avcC) con SPS/PPS H.264 |
| 0x83 | VIDEO_FRAME | PC → iPad | `u8 flag (bit0 = keyframe, bit1 = Annex B)`, `u32 timestamp ms`, dati H.264 (NAL con lunghezza a 4 byte, oppure Annex B se bit1) |
| 0x91 | STATUS | PC → iPad | testo UTF-8 da mostrare sull'iPad |

Coordinate TOUCH: `x`,`y` normalizzate 0…1 rispetto all'area del video.
Fasi: 0 giù, 1 movimento, 2 su, 3 annullato, 4 hover, 5 fine hover.
Tipi: 0 dito, 1 Apple Pencil, 2 puntatore (trackpad/mouse dell'iPad).
Gli id dei dita sono slot 0…9. In un messaggio TOUCH i campioni delle dita formano un unico
"frame" multi-touch; i campioni della Pencil vanno applicati in ordine.

Flusso: connessione → HELLO → (PC prepara schermo/encoder, eventuali STATUS) → CONFIG →
VIDEO_HEADER → VIDEO_FRAME… Ogni riavvio dell'encoder ripete CONFIG/VIDEO_HEADER e parte da un keyframe.
Entrambi i lati mandano un PING al secondo e chiudono dopo ~10 s di silenzio.
