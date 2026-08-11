using Fleck;

namespace QdlScan.Services;

/// <summary>
/// Ponte WebSocket fra il browser e l'app di scansione.
///
/// Hardening rispetto alla versione originale (che ascoltava su ws://0.0.0.0:8181
/// senza alcun controllo):
///  - bind solo su loopback (127.0.0.1): non più raggiungibile dalla LAN;
///  - validazione dell'header Origin: accetta solo pagine locali (localhost/127.0.0.1),
///    origini in allowlist esplicita, oppure contesto file:// (Origin "null");
///    qualunque sito remoto viene rifiutato.
/// </summary>
public sealed class ScanBridgeServer : IDisposable
{
    private const string ScanCommand = "1100";
    private const string StopCommand = "1101";

    // Messaggi di stato app->browser: indicano se lo scanner è occupato.
    private const string BusyMessage = "BUSY";
    private const string ReadyMessage = "READY";

    private readonly int _port;
    private readonly object _originGate = new();
    private HashSet<string> _allowedOrigins;
    private readonly List<IWebSocketConnection> _sockets = new();
    private readonly object _gate = new();
    private WebSocketServer? _server;
    private volatile bool _scanning;
    // Client che ha richiesto la scansione in corso: i risultati vanno solo a lui.
    private Guid? _activeRequesterId;

    /// <summary>Sollevato quando un client invia il comando di scansione.</summary>
    public event Action? ScanRequested;

    /// <summary>Sollevato quando un client invia il comando di interruzione.</summary>
    public event Action? StopRequested;

    /// <summary>Sollevato quando cambia il numero di client connessi.</summary>
    public event Action<int>? ClientsChanged;

    public ScanBridgeServer(int port, IEnumerable<string> allowedOrigins)
    {
        _port = port;
        _allowedOrigins = new HashSet<string>(allowedOrigins, StringComparer.OrdinalIgnoreCase);
    }

    public int ClientCount
    {
        get { lock (_gate) return _sockets.Count; }
    }

    /// <summary>Sostituisce l'allowlist delle origini a caldo (es. dopo una modifica dalla GUI),
    /// senza dover riavviare il bridge WebSocket.</summary>
    public void UpdateAllowedOrigins(IEnumerable<string> allowedOrigins)
    {
        lock (_originGate) _allowedOrigins = new HashSet<string>(allowedOrigins, StringComparer.OrdinalIgnoreCase);
    }

    public void Start()
    {
        FleckLog.Level = LogLevel.Warn;

        // Il bind su loopback è garantito dall'indirizzo 127.0.0.1 nella location.
        _server = new WebSocketServer($"ws://127.0.0.1:{_port}");

        _server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                var origin = socket.ConnectionInfo.Origin;
                HashSet<string> allowedOrigins;
                lock (_originGate) allowedOrigins = _allowedOrigins;
                if (!OriginPolicy.IsAllowed(origin, allowedOrigins))
                {
                    socket.Close();
                    return;
                }
                lock (_gate) _sockets.Add(socket);
                // Un browser che si collega a scansione in corso deve partire già "occupato".
                try { socket.Send(_scanning ? BusyMessage : ReadyMessage); } catch { /* best effort */ }
                RaiseClientsChanged();
            };

            socket.OnClose = () =>
            {
                lock (_gate) _sockets.Remove(socket);
                RaiseClientsChanged();
            };

            socket.OnError = _ =>
            {
                lock (_gate) _sockets.Remove(socket);
                RaiseClientsChanged();
            };

            socket.OnMessage = message =>
            {
                if (message == ScanCommand)
                {
                    // Ricorda chi ha chiesto la scansione: solo a lui torneranno le immagini.
                    lock (_gate) _activeRequesterId = socket.ConnectionInfo.Id;
                    ScanRequested?.Invoke();
                }
                else if (message == StopCommand)
                    StopRequested?.Invoke();
                // qualunque altro messaggio viene ignorato di proposito
            };
        });
    }

    /// <summary>
    /// Invia i byte di un'immagine acquisita al solo client che ha richiesto la scansione.
    /// Se quel client si è disconnesso nel frattempo, l'immagine viene scartata (non viene
    /// diffusa alle altre schede collegate, per riservatezza del documento).
    /// </summary>
    public void SendScanResult(byte[] data)
    {
        List<IWebSocketConnection> targets;
        lock (_gate)
        {
            targets = _activeRequesterId is Guid id
                ? _sockets.Where(s => s.ConnectionInfo.Id == id).ToList()
                : new List<IWebSocketConnection>();
        }

        foreach (var socket in targets)
        {
            try { socket.Send(data); }
            catch { /* socket caduto: verrà rimosso da OnClose/OnError */ }
        }
    }

    /// <summary>
    /// Aggiorna lo stato dello scanner (occupato/libero) e lo notifica a tutti i
    /// client, così che il browser blocchi nuove richieste mentre si scansiona.
    /// </summary>
    public void SetScanning(bool scanning)
    {
        _scanning = scanning;
        // A fine scansione dimentichiamo il richiedente: eventuali immagini tardive non
        // hanno più un destinatario e vengono scartate.
        if (!scanning)
            lock (_gate) _activeRequesterId = null;
        BroadcastText(scanning ? BusyMessage : ReadyMessage);
    }

    private void BroadcastText(string text)
    {
        List<IWebSocketConnection> snapshot;
        lock (_gate) snapshot = _sockets.ToList();

        foreach (var socket in snapshot)
        {
            try { socket.Send(text); }
            catch { /* socket caduto: verrà rimosso da OnClose/OnError */ }
        }
    }

    private void RaiseClientsChanged()
    {
        int count;
        lock (_gate) count = _sockets.Count;
        ClientsChanged?.Invoke(count);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var socket in _sockets)
            {
                try { socket.Close(); } catch { /* ignore */ }
            }
            _sockets.Clear();
        }
        _server?.Dispose();
        _server = null;
    }
}
