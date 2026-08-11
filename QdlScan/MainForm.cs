using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using QdlScan.Models;
using QdlScan.Services;

namespace QdlScan;

/// <summary>
/// Finestra principale (WinForms, grafica di default di Windows — sostituisce la
/// precedente MainWindow WinUI 3). La app parte nascosta nella tray: la finestra viene
/// creata ma mai mostrata finché l'utente non la apre o il browser non chiede una scansione.
/// </summary>
public sealed class MainForm : Form
{
    private enum Severity { Info, Warning, Error, Success }

    private sealed record ColorOption(ColorMode Value, string Label);
    private sealed record SourceOption(PaperSource Value, string Label);

    private readonly AppSettings _settings;
    private readonly WiaScannerService _wia = new();
    private readonly ScanBridgeServer _server;
    private readonly BindingList<ScannerInfo> _scanners = new();

    private bool _reallyExit;
    private bool _isScanning;
    private volatile bool _cancelRequested;
    private bool _selectedDeviceSupportsDuplex;
    // Evita di persistere le impostazioni durante la popolazione iniziale delle combo.
    private bool _optionsReady;
    // La finestra parte nascosta: SetVisibleCore intercetta la Show() implicita di Application.Run.
    private bool _allowVisible;

    // ---- controlli tray ----
    private NotifyIcon _trayIcon = null!;

    // ---- controlli form ----
    private Panel _statusPanel = null!;
    private Label _statusTitleLabel = null!;
    private Label _statusMessageLabel = null!;
    private ComboBox _scannerCombo = null!;
    private Button _refreshButton = null!;
    private ComboBox _dpiCombo = null!;
    private ComboBox _colorCombo = null!;
    private ComboBox _sourceCombo = null!;
    private CheckBox _duplexCheck = null!;
    private CheckBox _autostartCheck = null!;
    private Label _endpointLabel = null!;
    private ListBox _originsList = null!;
    private TextBox _originInput = null!;
    private Button _addOriginButton = null!;
    private Button _removeOriginButton = null!;
    private TextBox _versionLabel = null!;

    public MainForm()
    {
        InitializeComponents();
        // Forza la creazione dell'handle nativo prima che la finestra venga mostrata:
        // serve perché Invoke/BeginInvoke (usati dagli eventi del bridge WebSocket, che
        // arrivano su thread di background) richiedono un handle già esistente.
        _ = Handle;

        _settings = AppSettings.Load();

        ConfigureForm();
        PopulateStaticOptions();
        PopulateOriginsList();

        _endpointLabel.Text = $"WebSocket in ascolto su ws://127.0.0.1:{_settings.Port} (solo locale).";
        _autostartCheck.Checked = AutostartService.IsEnabled();

        // Avvio del bridge WebSocket (hardened: loopback + Origin allowlist).
        _server = new ScanBridgeServer(_settings.Port, _settings.AllowedOrigins);
        _server.ClientsChanged += OnClientsChanged;
        _server.ScanRequested += OnScanRequested;
        _server.StopRequested += OnStopRequested;
        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            SetStatus(Severity.Error, "Porta occupata",
                $"Impossibile avviare il bridge sulla porta {_settings.Port}: {ex.Message}");
        }

        _ = LoadScannersAsync();
    }

    // ==================== costruzione UI ====================

    private void InitializeComponents()
    {
        SuspendLayout();

        Text = $"Qdl Scan — {GetVersionText()}";
        ClientSize = new Size(680, 760);
        MinimumSize = new Size(520, 560);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Padding = new Padding(20);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var headerLabel = new Label
        {
            Text = "Acquisizione documenti da scanner verso il browser (WIA)",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Regular),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 12)
        };
        root.Controls.Add(headerLabel);

        // ---- barra di stato ----
        _statusPanel = new Panel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, 16),
            BorderStyle = BorderStyle.FixedSingle
        };
        var statusStack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
        _statusTitleLabel = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        _statusMessageLabel = new Label { AutoSize = true };
        statusStack.Controls.Add(_statusTitleLabel);
        statusStack.Controls.Add(_statusMessageLabel);
        _statusPanel.Controls.Add(statusStack);
        root.Controls.Add(_statusPanel);

        // ---- card impostazioni scanner ----
        var scannerGroup = new GroupBox
        {
            Text = "Impostazioni scanner",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 0, 0, 16)
        };

        var scannerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 0
        };
        scannerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        scannerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        // riga: scanner + aggiorna
        var scannerRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 10) };
        scannerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        scannerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var scannerColumn = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        scannerColumn.Controls.Add(new Label { Text = "Scanner", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _scannerCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Width = 300 };
        _scannerCombo.DisplayMember = null!;
        // SelectedIndexChanged (non SelectionChangeCommitted!) perché le capacità del
        // device vanno ricaricate anche quando la selezione è impostata da codice
        // (avvio, click "Aggiorna"), non solo quando l'utente sceglie dal dropdown.
        _scannerCombo.SelectedIndexChanged += ScannerCombo_SelectionChanged;
        scannerColumn.Controls.Add(_scannerCombo);
        _refreshButton = new Button { Text = "Aggiorna", AutoSize = true, Anchor = AnchorStyles.Bottom, Margin = new Padding(10, 0, 0, 0) };
        _refreshButton.Click += RefreshScanners_Click;
        scannerRow.Controls.Add(scannerColumn, 0, 0);
        scannerRow.Controls.Add(_refreshButton, 1, 0);
        scannerLayout.Controls.Add(scannerRow, 0, 0);
        scannerLayout.SetColumnSpan(scannerRow, 2);

        // riga: DPI / colore
        var dpiColumn = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 10) };
        dpiColumn.Controls.Add(new Label { Text = "Risoluzione (DPI)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _dpiCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        dpiColumn.Controls.Add(_dpiCombo);
        scannerLayout.Controls.Add(dpiColumn, 0, 1);

        var colorColumn = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 10) };
        colorColumn.Controls.Add(new Label { Text = "Colore", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _colorCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DisplayMember = nameof(ColorOption.Label) };
        colorColumn.Controls.Add(_colorCombo);
        scannerLayout.Controls.Add(colorColumn, 1, 1);

        // riga: origine / duplex
        var sourceColumn = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 10) };
        sourceColumn.Controls.Add(new Label { Text = "Origine", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _sourceCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DisplayMember = nameof(SourceOption.Label) };
        _sourceCombo.SelectionChangeCommitted += SourceCombo_SelectionChanged;
        sourceColumn.Controls.Add(_sourceCombo);
        scannerLayout.Controls.Add(sourceColumn, 0, 2);

        var duplexColumn = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 10) };
        duplexColumn.Controls.Add(new Label { Text = "Fronte/retro", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _duplexCheck = new CheckBox { Text = "Attivo", AutoSize = true, Enabled = false };
        duplexColumn.Controls.Add(_duplexCheck);
        scannerLayout.Controls.Add(duplexColumn, 1, 2);

        var captionLabel = new Label
        {
            Text = "Scansione e interruzione si avviano dal browser. Qui configuri lo scanner una sola volta.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0)
        };
        scannerLayout.Controls.Add(captionLabel, 0, 3);
        scannerLayout.SetColumnSpan(captionLabel, 2);

        scannerGroup.Controls.Add(scannerLayout);
        root.Controls.Add(scannerGroup);

        // ---- impostazioni avanzate ----
        var advancedGroup = new GroupBox
        {
            Text = "Impostazioni avanzate",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 12)
        };
        var advancedLayout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
        _autostartCheck = new CheckBox { Text = "Avvia all'accesso a Windows", AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        _autostartCheck.CheckedChanged += AutostartCheck_CheckedChanged;
        advancedLayout.Controls.Add(_autostartCheck);
        advancedLayout.Controls.Add(new Label
        {
            Text = "Mantiene il bridge sempre disponibile dopo il login.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        });
        _endpointLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        advancedLayout.Controls.Add(_endpointLabel);
        advancedGroup.Controls.Add(advancedLayout);
        root.Controls.Add(advancedGroup);

        // ---- domini autorizzati (Origin allowlist) ----
        var originsGroup = new GroupBox
        {
            Text = "Domini autorizzati",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 16, 0, 0)
        };
        var originsLayout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
        originsLayout.Controls.Add(new Label
        {
            Text = "Siti https:// da cui il bridge accetta connessioni (oltre a localhost, sempre ammesso).",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8)
        });

        _originsList = new ListBox { Dock = DockStyle.Top, Height = 90, Margin = new Padding(0, 0, 0, 8) };
        originsLayout.Controls.Add(_originsList);

        var originAddRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top };
        originAddRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        originAddRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        originAddRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _originInput = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        _addOriginButton = new Button { Text = "Aggiungi", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _addOriginButton.Click += AddOriginButton_Click;
        _removeOriginButton = new Button { Text = "Rimuovi selezionato", AutoSize = true };
        _removeOriginButton.Click += RemoveOriginButton_Click;
        originAddRow.Controls.Add(_originInput, 0, 0);
        originAddRow.Controls.Add(_addOriginButton, 1, 0);
        originAddRow.Controls.Add(_removeOriginButton, 2, 0);
        originsLayout.Controls.Add(originAddRow);

        originsGroup.Controls.Add(originsLayout);
        root.Controls.Add(originsGroup);

        // ---- versione ----
        // TextBox in sola lettura senza bordo invece di una Label: appare uguale ma il
        // testo resta selezionabile/copiabile (utile per confrontare la build in test).
        _versionLabel = new TextBox
        {
            Text = GetVersionText(),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Top,
            TabStop = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        root.Controls.Add(_versionLabel);

        Controls.Add(root);

        // ---- tray icon ----
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        var trayMenu = new ContextMenuStrip();
        var openItem = trayMenu.Items.Add("Apri");
        openItem.Click += (_, _) => ShowWindow();
        trayMenu.Items.Add(new ToolStripSeparator());
        var exitItem = trayMenu.Items.Add("Esci");
        exitItem.Click += (_, _) => ExitApp();

        _trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "Qdl Scan — bridge scanner per il browser",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        ResumeLayout(performLayout: true);
    }

    /// <summary>Configura la finestra: chiusura che minimizza in tray invece di uscire.</summary>
    private void ConfigureForm()
    {
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        FormClosing += OnFormClosing;

        SetStatus(Severity.Info, "In attesa", "Nessun browser collegato.");
    }

    /// <summary>Sopprime la Show() implicita di Application.Run: l'app parte nascosta in tray.</summary>
    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(value && _allowVisible);
    }

    // ---------------- combo statiche ----------------

    private static readonly int[] FallbackDpis = { 100, 150, 200, 300, 600 };
    private static readonly ColorMode[] FallbackColorModes =
        { ColorMode.Color, ColorMode.Grayscale, ColorMode.BlackWhite };

    private void PopulateStaticOptions()
    {
        _scannerCombo.DataSource = _scanners;
        _scannerCombo.DisplayMember = nameof(ScannerInfo.Name);

        // Popolamento iniziale con un elenco "ragionevole": verrà sostituito dai
        // valori realmente supportati non appena uno scanner viene selezionato
        // (vedi ApplyCapabilities, chiamato da ScannerCombo_SelectionChanged).
        ApplyCapabilities(null);

        _sourceCombo.Items.AddRange(new object[]
        {
            new SourceOption(PaperSource.Flatbed, "Piano fisso"),
            new SourceOption(PaperSource.Feeder, "Alimentatore (ADF)")
        });
        _sourceCombo.SelectedIndex = 0;

        // Da qui in poi le scelte dell'utente su DPI/colore vengono memorizzate.
        _optionsReady = true;
        _dpiCombo.SelectionChangeCommitted += (_, _) => PersistDefaults();
        _colorCombo.SelectionChangeCommitted += (_, _) => PersistDefaults();
    }

    /// <summary>Memorizza DPI e modalità colore correnti come default per i prossimi avvii.</summary>
    private void PersistDefaults()
    {
        if (!_optionsReady) return;
        if (_dpiCombo.SelectedItem is int dpi) _settings.DefaultDpi = dpi;
        if ((_colorCombo.SelectedItem as ColorOption)?.Value is ColorMode mode)
            _settings.DefaultColorMode = mode;
        _settings.Save();
    }

    /// <summary>
    /// Ripopola i combo DPI e colore con le capacità lette dal device (se note),
    /// altrimenti con l'elenco fallback. Preserva la selezione corrente quando
    /// il valore resta disponibile nel nuovo elenco, altrimenti ricade sul
    /// default salvato nelle impostazioni.
    /// </summary>
    private void ApplyCapabilities(ScannerCapabilities? caps)
    {
        int? previousDpi = _dpiCombo.SelectedItem as int?;
        IReadOnlyList<int> dpis = caps is not null && caps.Dpis.Count > 0 ? caps.Dpis : FallbackDpis;

        _dpiCombo.Items.Clear();
        _dpiCombo.Items.AddRange(dpis.Cast<object>().ToArray());
        int dpiIndex = previousDpi.HasValue ? _dpiCombo.Items.IndexOf(previousDpi.Value) : -1;
        if (dpiIndex < 0) dpiIndex = _dpiCombo.Items.IndexOf(_settings.DefaultDpi);
        if (dpiIndex < 0 && _dpiCombo.Items.Count > 0) dpiIndex = _dpiCombo.Items.Count / 2;
        if (_dpiCombo.Items.Count > 0) _dpiCombo.SelectedIndex = dpiIndex;

        ColorMode? previousColor = (_colorCombo.SelectedItem as ColorOption)?.Value;
        IReadOnlyList<ColorMode> colorModes =
            caps is not null && caps.ColorModes.Count > 0 ? caps.ColorModes : FallbackColorModes;

        _colorCombo.Items.Clear();
        _colorCombo.Items.AddRange(colorModes.Select(m => new ColorOption(m, ColorModeLabel(m))).Cast<object>().ToArray());
        int colorIndex = IndexOfColor(previousColor ?? _settings.DefaultColorMode);
        if (colorIndex < 0 && _colorCombo.Items.Count > 0) colorIndex = 0;
        if (_colorCombo.Items.Count > 0) _colorCombo.SelectedIndex = colorIndex;
    }

    private int IndexOfColor(ColorMode mode)
    {
        for (int i = 0; i < _colorCombo.Items.Count; i++)
            if ((_colorCombo.Items[i] as ColorOption)?.Value == mode) return i;
        return -1;
    }

    /// <summary>
    /// "Versione X.Y.Z+build.NNN": il numero di build (github.run_number, iniettato in
    /// CI con -p:BuildNumber) permette di verificare al volo se si sta testando l'ultima
    /// build o una precedente, senza dover confrontare orari/commit a mano.
    /// </summary>
    private static string GetVersionText()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "sconosciuta";
        return $"Versione {version}";
    }

    private static string ColorModeLabel(ColorMode mode) => mode switch
    {
        ColorMode.Color => "Colore",
        ColorMode.Grayscale => "Scala di grigi",
        ColorMode.BlackWhite => "Bianco e nero",
        _ => mode.ToString()
    };

    // ---------------- domini autorizzati ----------------

    private void PopulateOriginsList()
    {
        _originsList.Items.Clear();
        foreach (var origin in _settings.AllowedOrigins)
            _originsList.Items.Add(origin);
    }

    private void AddOriginButton_Click(object? sender, EventArgs e)
    {
        var text = _originInput.Text.Trim();
        if (text.Length == 0) return;

        // Normalizza a "schema://host[:porta]", scartando path/query eventualmente incollati.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            SetStatus(Severity.Warning, "Dominio non valido",
                "Inserisci un indirizzo completo, es. https://app.esempio.it");
            return;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (_settings.AllowedOrigins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)))
        {
            _originInput.Clear();
            return;
        }

        _settings.AllowedOrigins.Add(origin);
        _settings.Save();
        _server.UpdateAllowedOrigins(_settings.AllowedOrigins);
        PopulateOriginsList();
        _originInput.Clear();
    }

    private void RemoveOriginButton_Click(object? sender, EventArgs e)
    {
        if (_originsList.SelectedItem is not string origin) return;

        _settings.AllowedOrigins.RemoveAll(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        _server.UpdateAllowedOrigins(_settings.AllowedOrigins);
        PopulateOriginsList();
    }

    // ---------------- scanner ----------------

    private async Task LoadScannersAsync()
    {
        try
        {
            var found = await _wia.GetScannersAsync();
            _scanners.RaiseListChangedEvents = false;
            _scanners.Clear();
            foreach (var s in found) _scanners.Add(s);
            _scanners.RaiseListChangedEvents = true;
            _scanners.ResetBindings();

            if (_scanners.Count > 0)
                _scannerCombo.SelectedIndex = 0;
            else
                SetStatus(Severity.Warning, "Nessuno scanner",
                    "Nessuno scanner WIA rilevato. Collega un dispositivo e premi Aggiorna.");
        }
        catch (Exception ex)
        {
            SetStatus(Severity.Error, "Errore WIA", ex.Message);
        }
    }

    private async void RefreshScanners_Click(object? sender, EventArgs e)
        => await LoadScannersAsync();

    private async void ScannerCombo_SelectionChanged(object? sender, EventArgs e)
    {
        _selectedDeviceSupportsDuplex = false;
        if (_scannerCombo.SelectedItem is ScannerInfo info)
        {
            try { _selectedDeviceSupportsDuplex = await _wia.SupportsDuplexAsync(info.DeviceId); }
            catch { _selectedDeviceSupportsDuplex = false; }

            try
            {
                var caps = await _wia.GetCapabilitiesAsync(info.DeviceId);
                ApplyCapabilities(caps);
            }
            catch (Exception ex)
            {
                ApplyCapabilities(null);
                SetStatus(Severity.Warning, "Capacità scanner non lette",
                    $"Uso i valori DPI/colore predefiniti: {ex.Message}");
            }
        }
        else
        {
            ApplyCapabilities(null);
        }
        UpdateDuplexAvailability();
    }

    private void SourceCombo_SelectionChanged(object? sender, EventArgs e)
        => UpdateDuplexAvailability();

    private void UpdateDuplexAvailability()
    {
        bool feeder = (_sourceCombo.SelectedItem as SourceOption)?.Value == PaperSource.Feeder;
        bool enabled = feeder && _selectedDeviceSupportsDuplex;
        _duplexCheck.Enabled = enabled;
        if (!enabled) _duplexCheck.Checked = false;
    }

    // ---------------- scansione (guidata dal browser) ----------------

    private async Task StartScanAsync()
    {
        if (_isScanning) return;

        if (_scannerCombo.SelectedItem is not ScannerInfo scanner)
        {
            SetStatus(Severity.Warning, "Scanner non selezionato",
                "Seleziona uno scanner prima di avviare la scansione.");
            ShowWindow();
            return;
        }

        var options = new ScanOptions
        {
            DeviceId = scanner.DeviceId,
            Dpi = _dpiCombo.SelectedItem is int dpi ? dpi : 300,
            ColorMode = (_colorCombo.SelectedItem as ColorOption)?.Value ?? ColorMode.Color,
            Source = (_sourceCombo.SelectedItem as SourceOption)?.Value ?? PaperSource.Flatbed,
            Duplex = _duplexCheck.Checked
        };

        SetScanningState(true);
        SetStatus(Severity.Info, "Scansione in corso", "Acquisizione dallo scanner…");

        int pageCount = 0;
        IReadOnlyList<string> rejectedSettings = Array.Empty<string>();
        try
        {
            await _wia.ScanAsync(
                options,
                onPage: bytes =>
                {
                    pageCount++;
                    // Invio immediato al solo browser che ha richiesto la scansione (thread-safe).
                    _server.SendScanResult(bytes);
                },
                isCancelled: () => _cancelRequested,
                onSettingsRejected: rejected => rejectedSettings = rejected);

            string rejectedSuffix = rejectedSettings.Count > 0
                ? $" Attenzione: lo scanner ha rifiutato {string.Join(", ", rejectedSettings)} e ha usato i propri valori correnti. " +
                  "Dettagli in %AppData%\\QdlScan\\wia-diagnostics.log."
                : string.Empty;

            if (_cancelRequested)
                SetStatus(Severity.Warning, "Interrotta", $"Scansione interrotta dopo {pageCount} pagina/e.{rejectedSuffix}");
            else if (pageCount == 0)
                SetStatus(Severity.Warning, "Nessuna pagina", $"Lo scanner non ha prodotto immagini.{rejectedSuffix}");
            else if (rejectedSettings.Count > 0)
                SetStatus(Severity.Warning, "Completata con avvisi",
                    $"{pageCount} pagina/e acquisita/e e inviata/e al browser.{rejectedSuffix}");
            else
                SetStatus(Severity.Success, "Completata", $"{pageCount} pagina/e acquisita/e e inviata/e al browser.");
        }
        catch (Exception ex)
        {
            SetStatus(Severity.Error, "Errore di scansione", ex.Message);
        }
        finally
        {
            SetScanningState(false);
        }
    }

    private void SetScanningState(bool scanning)
    {
        _isScanning = scanning;
        if (scanning) _cancelRequested = false;
        // Durante la scansione si blocca il cambio scanner (lo scan è guidato dal browser).
        _scannerCombo.Enabled = !scanning;
        // Notifica ai browser collegati: occupato/libero, così bloccano altre richieste.
        _server.SetScanning(scanning);
    }

    // ---------------- eventi bridge (arrivano su thread di background: serve marshaling) ----------------

    private void OnClientsChanged(int count)
        => RunOnUiThread(() =>
        {
            if (count > 0)
                SetStatus(Severity.Success, "Browser collegato",
                    $"{count} pagina/e web in ascolto. Pronto a scansionare.");
            else
                SetStatus(Severity.Info, "In attesa", "Nessun browser collegato.");
        });

    private void OnScanRequested()
        => RunOnUiThread(() =>
        {
            // Scanner già configurato: scansione in background, senza aprire la finestra
            // (si lavora esclusivamente dal browser).
            if (_scannerCombo.SelectedItem is ScannerInfo)
            {
                if (!_isScanning) _ = StartScanAsync();
            }
            else
            {
                // Prima configurazione: mostra la finestra per selezionare lo scanner.
                ShowWindow();
                SetStatus(Severity.Warning, "Scanner non selezionato",
                    "Seleziona uno scanner prima di avviare la scansione dal browser.");
            }
        });

    private void OnStopRequested()
        => RunOnUiThread(() =>
        {
            if (_isScanning) _cancelRequested = true;
        });

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    // ---------------- tray / finestra ----------------

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyExit) return;
        // La X minimizza nella tray invece di chiudere (come l'app originale).
        e.Cancel = true;
        Hide();
    }

    private void ShowWindow()
    {
        _allowVisible = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _server.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Close();
    }

    // ---------------- autostart ----------------

    private void AutostartCheck_CheckedChanged(object? sender, EventArgs e)
    {
        try
        {
            AutostartService.SetEnabled(_autostartCheck.Checked);
        }
        catch (Exception ex)
        {
            SetStatus(Severity.Error, "Autostart", ex.Message);
        }
    }

    // ---------------- helper UI ----------------

    private void SetStatus(Severity severity, string title, string message)
    {
        _statusTitleLabel.Text = title;
        _statusMessageLabel.Text = message;

        (Color back, Color fore) = severity switch
        {
            Severity.Warning => (Color.FromArgb(255, 244, 206), Color.FromArgb(96, 72, 0)),
            Severity.Error => (Color.FromArgb(253, 231, 233), Color.FromArgb(140, 20, 24)),
            Severity.Success => (Color.FromArgb(223, 246, 221), Color.FromArgb(15, 92, 24)),
            _ => (Color.FromArgb(230, 244, 255), Color.FromArgb(20, 70, 120))
        };
        _statusPanel.BackColor = back;
        _statusTitleLabel.ForeColor = fore;
        _statusMessageLabel.ForeColor = fore;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
