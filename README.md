# PadScreen — l'iPad come secondo schermo per Windows

PadScreen trasforma l'iPad in un **monitor in più** per il PC Windows, collegato **con il cavo USB**
oppure **in Wi-Fi** sulla rete di casa. Il desktop di Windows si estende sull'iPad; dita, Apple Pencil
(con pressione e inclinazione) e trackpad dell'iPad comandano il PC.

```
 PC Windows                                                     iPad
 ┌───────────────────────────────────────────┐                  ┌──────────────────────┐
 │ Driver Parsec VDD → monitor virtuale      │                  │ App PadScreen        │
 │ FFmpeg: cattura (ddagrab) + H.264 hardware│ ── video ──────▶ │ decodifica hardware  │
 │ PadScreen.exe                             │   USB (usbmux)   │ (VideoToolbox)       │
 │  • invia il video                         │   o Wi-Fi (TCP)  │                      │
 │  • inietta touch / penna / mouse ◀────────┼──── input ────── │ tocchi, Pencil, hover│
 └───────────────────────────────────────────┘                  └──────────────────────┘
```

## Download

**Programma per Windows (x64, un solo file, .NET incluso):**
[PadScreen.exe](https://github.com/rikysec/PadScreen/releases/latest/download/PadScreen.exe) —
tutte le versioni nella pagina [Releases](https://github.com/rikysec/PadScreen/releases).

L'app per iPad si installa da Xcode (vedi sotto).

## Contenuto del repository

| Percorso | Cosa contiene |
|---|---|
| `ipad/` | App per iPad (SwiftUI, iPadOS 17+). Progetto Xcode generato da `project.yml` |
| `windows/PadScreen/` | Sorgenti del programma Windows (C# .NET 10, WinForms) |
| `windows/PadScreen.Core/` | Protocollo, sessione, ricerca Bonjour, tunnel USB (multipiattaforma) |
| `windows/PadScreen.DevHost/` | Host di prova per macOS: manda un video di test all'app iPad |
| `PROTOCOL.md` | Descrizione del protocollo di rete |
| `REVISIONE.md` | Note tecniche per chi deve rivedere/ottimizzare il codice |
| `PadScreen.sln`, `build.ps1` | Solution per Visual Studio e script di compilazione per Windows |

## Cosa serve sul PC

1. **Windows 10 21H2 o successivo / Windows 11, 64 bit.**
2. **FFmpeg 6.1 o successivo** — nel programma c'è il pulsante *Installa FFmpeg (winget)*, che esegue
   ```
   winget install -e --id Gyan.FFmpeg
   ```
3. **Driver Parsec Virtual Display** (gratuito, firmato) per avere un vero schermo *esteso*:
   [parsec-vdd-0.45.0.0.exe](https://builds.parsec.app/vdd/parsec-vdd-0.45.0.0.exe).
   Su installazioni di Windows 10 dove la 0.45 dà problemi usa la
   [0.41](https://builds.parsec.app/vdd/parsec-vdd-0.41.0.0.exe).
   Senza driver puoi comunque usare la modalità *Duplica un monitor esistente*.
4. **Solo per il cavo USB:** l'app **Dispositivi Apple** (Microsoft Store) oppure iTunes: installano
   il servizio *Apple Mobile Device Service* che PadScreen usa per parlare con l'iPad via USB.

## Installare l'app sull'iPad

Serve un Mac con Xcode (la stessa procedura delle altre tue app; il team di firma `TF8U6L5RE8` è già impostato).

```bash
cd PadScreen/ipad
xcodegen generate
open PadScreen.xcodeproj
```

In Xcode scegli il tuo iPad come destinazione e premi **Run**. Al primo avvio l'iPad chiede di
consentire l'accesso alla **rete locale**: rispondi *Consenti* (serve per il Wi-Fi; il cavo funziona comunque).

## Uso

1. Apri **PadScreen sull'iPad** e lasciala in primo piano.
2. Avvia **PadScreen.exe** sul PC.
3. Nell'elenco scegli l'iPad (*Cavo USB* oppure *Wi-Fi*) o scrivi l'indirizzo IP mostrato sull'iPad, poi **Connetti**.
4. La prima volta:
   - Windows chiede il permesso di amministratore per registrare nel driver la risoluzione esatta dell'iPad
     (una volta sola per modello di iPad);
   - PadScreen prova gli encoder video disponibili (NVIDIA, AMD, Intel, Media Foundation, software) e ricorda quello che funziona.
5. In **Impostazioni di Windows → Sistema → Schermo** puoi trascinare il nuovo schermo dove sta fisicamente l'iPad
   e impostare la **scala al 200%** (con la risoluzione "Nativa", altrimenti il testo è minuscolo).

Per smettere premi **Disconnetti**: lo schermo virtuale sparisce e le finestre tornano sugli altri monitor.
Se il collegamento cade (cavo staccato, iPad bloccato), PadScreen riprova da solo per un minuto.

### Comandi dall'iPad

| Sull'iPad | Su Windows |
|---|---|
| Tocco con un dito | Tocco / clic (come uno schermo touch) |
| Tieni premuto | Clic destro |
| Due dita | Scorrimento e zoom |
| Apple Pencil | Penna con pressione e inclinazione (OneNote, Photoshop, Krita, Whiteboard…) |
| Pencil sospesa (iPad compatibili) | Movimento del cursore della penna senza toccare |
| Trackpad / mouse collegato all'iPad | Puntatore, clic e scorrimento |

In *Input dall'iPad* puoi scegliere la modalità **Mouse**: un dito muove il puntatore e trascinare equivale a tenere premuto il tasto sinistro.

## Qualità e latenza

- Il **cavo USB** è la scelta più stabile. In Wi-Fi usa la rete a 5 GHz e tieni il PC via cavo Ethernet se puoi.
- **Bitrate**: 20 Mbit/s va bene per lavoro d'ufficio; alza a 40–60 con il cavo. Se la rete non regge,
  PadScreen abbassa il bitrate da solo (lo vedi nel registro).
- **Risoluzione "Metà"**: più leggera per PC vecchi o Wi-Fi debole, un po' meno nitida.
- Sull'iPad l'interruttore *Mostra statistiche* mostra fps, banda e latenza di rete.

## Risoluzione dei problemi

| Problema | Cosa fare |
|---|---|
| L'iPad non compare nell'elenco Wi-Fi | Stessa rete per PC e iPad; sull'iPad *Impostazioni → Privacy → Rete locale → PadScreen* attivo; alcune reti "ospiti" isolano i dispositivi: scrivi l'IP a mano. Controlla che il firewall di Windows non blocchi PadScreen. |
| «Servizio Apple Mobile Device non trovato» | Installa *Dispositivi Apple* dal Microsoft Store (o iTunes). |
| L'iPad via USB non compare o rifiuta | Sblocca l'iPad e tocca *Autorizza* quando chiede di fidarsi del computer; l'app PadScreen deve essere aperta. |
| Schermo nero / errore encoder | Apri il registro (*Apri registro*). Prova *Encoder: Software (x264)* e 30 FPS. |
| Testo minuscolo sull'iPad | Scala dello schermo virtuale al 200% nelle impostazioni di Windows, oppure risoluzione "Metà". |
| Il tocco non funziona su finestre "amministratore" | Windows blocca l'input da programmi non elevati: avvia PadScreen.exe come amministratore. |

Registro completo: `%APPDATA%\PadScreen\padscreen.log` — impostazioni: `%APPDATA%\PadScreen\settings.json`.

## Limiti noti

- L'app sull'iPad deve restare **in primo piano**: iPadOS sospende le app in background.
- L'audio resta sul PC.
- La modalità *Duplica* trasmette il monitor alla sua risoluzione: su un monitor 16:9 l'iPad mostra bande nere.

## Stato dei test

- App iPad e libreria comune: provate end-to-end sul simulatore iPad (video 60 fps fino a 2360×1640,
  tocchi con coordinate esatte, rotazione, encoder hardware e software, ricerca Bonjour, protocollo usbmux).
- Programma Windows: compila senza avvisi e le strutture Win32 sono verificate byte per byte, ma **non è ancora
  stato eseguito su un PC Windows reale**. Al primo avvio, se qualcosa non va, il file di registro dice dove.

## Compilare da sorgente

Programma Windows, direttamente sul PC (serve .NET 10 SDK: `winget install -e --id Microsoft.DotNet.SDK.10`):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Oppure da qualsiasi sistema (anche macOS/Linux):

```bash
dotnet publish windows/PadScreen/PadScreen.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist
```

Per PC Windows ARM (Snapdragon) usa `-r win-arm64`.

Host di prova su macOS (con l'app iPad aperta nel simulatore):

```bash
dotnet run --project windows/PadScreen.DevHost -- --host 127.0.0.1 --seconds 20
```
