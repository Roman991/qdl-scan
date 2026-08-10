# ScanAppForWeb

Acquisisci documenti da uno **scanner fisico direttamente dal browser**. Poiché
JavaScript non può accedere all'hardware, una piccola app desktop fa da "ponte":
resta in background e comunica con la pagina web via **WebSocket**.

> **Versione 2.0** — l'app desktop è stata riscritta in **WinUI 3 / .NET 10** e usa
> **WIA (Windows Image Acquisition)** al posto di TWAIN. Vedi sotto le note di migrazione.

## Architettura

```
Browser  ──WebSocket ws://127.0.0.1:8787──►  App desktop (WinUI 3)  ──WIA──►  Scanner
  │  "1100"  → richiesta scansione                 (NewScan.exe)
  │  ◄── Blob immagine (binario)
```

1. L'app desktop parte minimizzata nella **system tray** e apre un server WebSocket
   **solo su loopback** (`127.0.0.1:8787`).
2. La pagina web si collega. Se non riesce, mostra l'invito a installare l'app.
3. Click su **Scan** nel browser → invia `"1100"`.
4. L'app acquisisce via WIA e rispedisce l'immagine come **Blob** al browser, che ne fa
   l'anteprima ed eventualmente la carica su un server.

## Contenuto del repository

| Percorso | Ruolo | Stack |
|----------|-------|-------|
| `NewScan/` | App desktop "ponte" | C# **.NET 10**, **WinUI 3**, Fleck (WebSocket), WIA |
| `example.html` | Esempio client puro (anteprima + rotazione) | HTML + JS |
| `installer/` | Script **Inno Setup** per generare `ScanAppSetup.exe` | Inno Setup |

## Build dell'app desktop

Requisiti: **.NET 10 SDK** (con WindowsAppSDK 2.2 via NuGet).

```sh
# Pubblicazione self-contained (nessun prerequisito runtime per l'utente).
# In self-contained WinUI il platform deve essere esplicito (x64), non AnyCPU.
dotnet publish NewScan/NewScan.csproj -c Release -r win-x64 --self-contained -p:Platform=x64
```

Output: `NewScan/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/`.

Per generare l'installer (richiede [Inno Setup](https://jrsoftware.org/isinfo.php)):

```sh
cd installer
ISCC setup.iss        # produce installer/Output/ScanAppSetup.exe
```

## Integrazione in una pagina web

Pulsante per avviare la scansione e contenitore per le anteprime:

```html
<button type="button" onclick="scanImage();" class="btn btn-primary btn-lg">Scan</button>
<div id="selectedFiles" class="row" style="padding: 3px"></div>
```

Logica WebSocket essenziale (riconnessione + stato occupato/libero):

```javascript
const BRIDGE_URL = 'ws://127.0.0.1:8787/';   // solo loopback
let ws = null;

function connect() {
    ws = new WebSocket(BRIDGE_URL);
    ws.binaryType = 'blob';

    ws.addEventListener('message', (e) => {
        if (typeof e.data === 'string') {
            // Stato dello scanner: "BUSY" = occupato (blocca Scan), "READY" = libero.
            return;
        }
        if (e.data instanceof Blob) {
            // e.data è il JPEG di una pagina: mostralo o caricalo su un server.
        }
    });

    // L'app può avviarsi/riavviarsi dopo: riprova la connessione.
    ws.addEventListener('close', () => setTimeout(connect, 3000), { once: true });
    ws.addEventListener('error', () => ws.close());
}

function scanImage() {
    if (ws?.readyState === WebSocket.OPEN) ws.send('1100');   // "1101" = interrompi
}

connect();
```

Esempio completo (anteprima + rotazione, gestione `BUSY`/`READY` e riconnessione):
[`example.html`](example.html).

## Funzionalità dell'app desktop

- Selezione scanner, **DPI**, **colore/grigi/bianco-nero**, **origine** (piano/ADF) e **fronte/retro**.
- Anteprima dell'ultima pagina, stato connessione browser, interruzione scansione.
- **Icona nella tray**; la X minimizza, l'uscita avviene dal menu della tray.
- **Avvio automatico** all'accesso: opzione **esplicita** nelle impostazioni avanzate
  (non più attivata silenziosamente come nella v1).

## Sicurezza

- WebSocket in ascolto **solo su `127.0.0.1`** (non più su `0.0.0.0`): non raggiungibile dalla LAN.
- **Validazione dell'header Origin**: accettate solo pagine locali (`localhost`/`127.0.0.1`),
  contesti `file://` e origini in allowlist; i siti remoti vengono rifiutati. Questo blocca il
  *Cross-Site WebSocket Hijacking* da un sito web ostile.
- **I risultati della scansione vengono inviati solo al client che l'ha richiesta**, non a
  tutte le schede collegate.

**Limite noto del modello di minaccia**: un client locale che non invia l'header `Origin`
(es. uno script `curl` o un altro processo sulla stessa macchina) viene accettato, perché lo
stesso comportamento serve ai contesti `file://`. Essendo l'ascolto su loopback, la minaccia è
limitata a software già in esecuzione localmente. Per scenari più sensibili valutare un token
condiviso nell'handshake.

Allowlist e porta sono configurabili in `%AppData%\ScanApp\settings.json` (il file viene
creato ai valori predefiniti al primo avvio).

## Note di migrazione (v1 → v2)

- **TWAIN/NTwain → WIA**: il duplex/ADF dipende dal driver WIA del dispositivo. Dove non
  supportato, l'opzione fronte/retro è disabilitata e si acquisisce in singola pagina.
- **WinForms/.NET 4.5.2 → WinUI 3/.NET 10** (WindowsAppSDK 2.2).
- **Setup `.vdproj`/MSI → Inno Setup** (`ScanAppSetup.exe`), output self-contained.
