# Note tecniche per la revisione

Documento per chi (persona o IA) deve rivedere, ottimizzare o adattare PadScreen al PC Windows su cui verrà installato.
Panoramica d'uso in [README.md](README.md), protocollo di rete in [PROTOCOL.md](PROTOCOL.md).

## Architettura in breve

```
iPad (ipad/)                                PC Windows (windows/)
───────────────────────────────             ──────────────────────────────────────────────────
Server.swift      NWListener :47800 ◀─TCP── AppController.OpenTransportAsync (Wi-Fi)
                  Bonjour _padscreen._tcp ◀─ UsbMux.cs (USB: Apple Mobile Device Service :27015)
H264Depacketizer  avcC / AVCC / Annex B     StreamSession.cs   handshake, ping, coda di invio, riavvii
VideoPipeline     AVSampleBufferDisplayLayer FfmpegProcess + FlvReader   FFmpeg → FLV su stdout
StreamView        tocchi/Pencil/hover/scroll  FfmpegSetup.cs   scelta encoder e argomenti FFmpeg
                                            ParsecVdd.cs     driver monitor virtuale (IOCTL + keepalive)
                                            Displays.cs      modalità video, uscite DXGI per ddagrab
                                            InputInjector.cs InjectTouchInput, penna sintetica, SendInput
```

- `windows/PadScreen.Core` è multipiattaforma (net10.0) ed è la parte testata end-to-end.
- `windows/PadScreen` è specifico Windows (net10.0-windows, WinForms) e dipende da Win32.
- `windows/PadScreen.DevHost` è un host di prova da riga di comando che usa la stessa `StreamSession`.

## Stato delle verifiche

| Parte | Stato |
|---|---|
| App iPad | Compila senza avvisi (Xcode 27, iOS 17+). Provata nel simulatore iPad Air 11" M3. |
| Protocollo, sessione, FLV, Bonjour, usbmux | Provati end-to-end su macOS con l'app nel simulatore: 60+ fps a 2360×1640 con h264_videotoolbox e libx264, coordinate dei tocchi esatte, rotazione (SIZE → riconfigurazione → nuovo keyframe), chiusura con BYE. usbmux: `ListDevices` verificato contro usbmuxd di macOS. |
| Strutture Win32 (Native.cs) | Dimensioni e offset verificati con `Marshal.SizeOf`/`OffsetOf` contro l'ABI x64 (POINTER_TYPE_INFO 152, INPUT 40, DEVMODE 220…). |
| Programma Windows | Compila senza avvisi (cross-compilato da macOS). **Mai eseguito su un PC Windows reale.** |
| Revisione codice Windows | Già fatta una revisione indipendente; correzioni applicate: UP del tocco/penna nella posizione dell'ultimo frame, coordinate della penna relative al desktop virtuale, keepalive della penna, ritentativo su ERROR_NOT_READY, fine del ciclo di riconnessione, gestione blocco schermo/UAC, prova encoder identica alla trasmissione, serializzazione dello schermo virtuale. |

## Dove intervenire per ottimizzare su un PC specifico

1. **Encoder e parametri FFmpeg** — `windows/PadScreen/FfmpegSetup.cs`, metodo `BuildArgs`.
   - Ordine di prova degli encoder: array `All`. L'encoder scelto viene memorizzato in
     `%APPDATA%\PadScreen\settings.json` (`EncoderCache`): cancellare quella voce per rifare la prova.
   - Percorsi: `Gpu` (ddagrab → encoder su texture D3D11, zero copie), `GpuQsv`, `Cpu` (hwdownload + swscale), `Gdi`.
   - Per NVIDIA: `-preset p1 -tune ull -zerolatency 1 -rc cbr`. Per AMD: `-usage ultralowlatency -quality speed`.
   - GOP = 4 s (`gop = fps * 4`), VBV = metà del bitrate al secondo.
2. **Bitrate/FPS/risoluzione predefiniti** — `windows/PadScreen/Settings.cs`.
3. **Controllo congestione** — `PadScreen.Core/StreamSession.cs`: `MaxPending` (fotogrammi in coda prima di
   ridurre il bitrate del 30% e riavviare l'encoder).
4. **PC con due GPU (portatili ibridi)** — `Displays.FindDxgiOutput` trova scheda e uscita dello schermo; FFmpeg riceve
   `-init_hw_device d3d11va=dx:<scheda>` e `ddagrab=output_idx=<uscita>`. Se Windows assegna a ffmpeg.exe una GPU
   diversa (Impostazioni → Grafica), l'ordine delle schede può cambiare: è il primo punto da controllare se lo schermo resta nero.
5. **Input** — `InputInjector.cs`. Touch via `InjectTouchInput` (coordinate fisiche del desktop virtuale, il processo è
   PerMonitorV2), penna via `CreateSyntheticPointerDevice(PT_PEN)` (coordinate relative all'origine del desktop virtuale),
   mouse via `SendInput` assoluto `VIRTUALDESK`.

## Punti da verificare per primi su Windows

- Il driver Parsec VDD crea lo schermo (`ParsecVdd.AddDisplay`) e `Displays.ParsecDevices` lo riconosce
  (cerca "Parsec Virtual Display" in `DISPLAY_DEVICE.DeviceString`).
- La risoluzione personalizzata scritta in `HKLM\SOFTWARE\Parsec\vdd\{0..4}` (width/height/hz, DWORD) compare tra le
  modalità dello schermo dopo averlo ricollegato.
- `ddagrab` cattura lo schermo virtuale con l'encoder hardware (vedi registro: "Encoder scelto: …").
- Tap = clic, pressione prolungata = clic destro, Pencil disegna con pressione in un'app tipo Paint/OneNote.
- Via cavo: `UsbMux.ListDevicesAsync` vede l'iPad (serve l'app "Dispositivi Apple" o iTunes) e `Connect` sulla porta 47800 riesce.

Il registro completo è in `%APPDATA%\PadScreen\padscreen.log` (pulsante *Apri registro*).

## Vincoli da rispettare

- Il protocollo è condiviso con l'app iPad: se si cambia un messaggio in `PadScreen.Core/Protocol.cs` va cambiato
  anche `ipad/PadScreen/Protocol.swift` (e `PROTOCOL.md`).
- L'output di FFmpeg deve restare **FLV su stdout** con H.264 (profilo high/main, 4:2:0, senza B-frame):
  è ciò che `FlvReader` e il decoder dell'iPad si aspettano.
- Lo schermo virtuale resta vivo solo finché il thread di keepalive in `ParsecVdd` invia IOCTL_UPDATE (< 100 ms).

## Compilare

- Windows: `powershell -ExecutionPolicy Bypass -File .\build.ps1` (serve .NET SDK 10) → `dist\PadScreen.exe`.
  In alternativa aprire `PadScreen.sln` con Visual Studio 2022/2026.
- iPad: `cd ipad && xcodegen generate && open PadScreen.xcodeproj` (il progetto Xcode generato è già nel repository).
- Prova senza Windows (macOS + simulatore): `dotnet run --project windows/PadScreen.DevHost -- --host 127.0.0.1`.
