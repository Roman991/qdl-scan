using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NewScan.Models;
using NewScan.Services;

namespace NewScan;

public sealed partial class MainWindow : Window
{
    private sealed record ColorOption(ColorMode Value, string Label);
    private sealed record SourceOption(PaperSource Value, string Label);

    private readonly AppSettings _settings;
    private readonly WiaScannerService _wia = new();
    private readonly ScanBridgeServer _server;
    private readonly ObservableCollection<ScannerInfo> _scanners = new();

    private AppWindow _appWindow = null!;
    private bool _reallyExit;
    private bool _isScanning;
    private volatile bool _cancelRequested;
    private bool _selectedDeviceSupportsDuplex;
    // Evita di persistere le impostazioni durante la popolazione iniziale delle combo.
    private bool _optionsReady;

    public MainWindow()
    {
        InitializeComponent();

        // I comandi della tray vengono assegnati in code-behind (non via {x:Bind}):
        // con ContextMenuMode predefinito (PopupMenu) il menu è nativo e ignora gli
        // handler Click dei MenuFlyoutItem — risponde solo al Command. Assegnandolo qui,
        // dopo InitializeComponent, evitiamo ogni problema di timing dei binding OneTime.
        TrayIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        OpenMenuItem.Command = new RelayCommand(ShowWindow);
        ExitMenuItem.Command = new RelayCommand(ExitApp);

        // La finestra parte nascosta: forziamo la creazione dell'icona nella tray,
        // altrimenti il Loaded del contenuto XAML potrebbe non scattare mai.
        TrayIcon.ForceCreate();

        _settings = AppSettings.Load();

        ConfigureAppWindow();
        PopulateStaticOptions();

        EndpointText.Text = $"WebSocket in ascolto su ws://127.0.0.1:{_settings.Port} (solo locale).";
        AutostartToggle.IsOn = AutostartService.IsEnabled();

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
            SetStatus(InfoBarSeverity.Error, "Porta occupata",
                $"Impossibile avviare il bridge sulla porta {_settings.Port}: {ex.Message}");
        }

        _ = LoadScannersAsync();
    }

    /// <summary>Configura la finestra: dimensioni, intercetta la chiusura per minimizzare in tray.</summary>
    private void ConfigureAppWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _appWindow.Resize(new Windows.Graphics.SizeInt32(720, 880));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsMaximizable = false;

        // Icona di sistema/taskbar (sostituisce quella generica di default).
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
            _appWindow.SetIcon(iconPath);

        // Title bar personalizzata in stile WinUI: il contenuto si estende nella barra.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _appWindow.Closing += OnAppWindowClosing;
    }

    /// <summary>Chiamata da App.OnLaunched: l'app parte nascosta nella tray.</summary>
    public void StartHidden() => _appWindow.Hide();

    // ---------------- combo statiche ----------------

    private void PopulateStaticOptions()
    {
        ScannerCombo.ItemsSource = _scanners;

        DpiCombo.ItemsSource = new[] { 100, 150, 200, 300, 600 };
        DpiCombo.SelectedItem = _settings.DefaultDpi;
        if (DpiCombo.SelectedItem is null) DpiCombo.SelectedIndex = 3;

        ColorCombo.ItemsSource = new[]
        {
            new ColorOption(ColorMode.Color, "Colore"),
            new ColorOption(ColorMode.Grayscale, "Scala di grigi"),
            new ColorOption(ColorMode.BlackWhite, "Bianco e nero")
        };
        ColorCombo.SelectedIndex = (int)_settings.DefaultColorMode;

        SourceCombo.ItemsSource = new[]
        {
            new SourceOption(PaperSource.Flatbed, "Piano fisso"),
            new SourceOption(PaperSource.Feeder, "Alimentatore (ADF)")
        };
        SourceCombo.SelectedIndex = 0;

        // Da qui in poi le scelte dell'utente su DPI/colore vengono memorizzate.
        _optionsReady = true;
        DpiCombo.SelectionChanged += (_, _) => PersistDefaults();
        ColorCombo.SelectionChanged += (_, _) => PersistDefaults();
    }

    /// <summary>Memorizza DPI e modalità colore correnti come default per i prossimi avvii.</summary>
    private void PersistDefaults()
    {
        if (!_optionsReady) return;
        if (DpiCombo.SelectedItem is int dpi) _settings.DefaultDpi = dpi;
        if ((ColorCombo.SelectedItem as ColorOption)?.Value is ColorMode mode)
            _settings.DefaultColorMode = mode;
        _settings.Save();
    }

    // ---------------- scanner ----------------

    private async Task LoadScannersAsync()
    {
        try
        {
            var found = await _wia.GetScannersAsync();
            _scanners.Clear();
            foreach (var s in found) _scanners.Add(s);

            if (_scanners.Count > 0)
                ScannerCombo.SelectedIndex = 0;
            else
                SetStatus(InfoBarSeverity.Warning, "Nessuno scanner",
                    "Nessuno scanner WIA rilevato. Collega un dispositivo e premi Aggiorna.");
        }
        catch (Exception ex)
        {
            SetStatus(InfoBarSeverity.Error, "Errore WIA", ex.Message);
        }
    }

    private async void RefreshScanners_Click(object sender, RoutedEventArgs e)
        => await LoadScannersAsync();

    private async void ScannerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDeviceSupportsDuplex = false;
        if (ScannerCombo.SelectedItem is ScannerInfo info)
        {
            try { _selectedDeviceSupportsDuplex = await _wia.SupportsDuplexAsync(info.DeviceId); }
            catch { _selectedDeviceSupportsDuplex = false; }
        }
        UpdateDuplexAvailability();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDuplexAvailability();

    private void UpdateDuplexAvailability()
    {
        bool feeder = (SourceCombo.SelectedItem as SourceOption)?.Value == PaperSource.Feeder;
        bool enabled = feeder && _selectedDeviceSupportsDuplex;
        DuplexToggle.IsEnabled = enabled;
        if (!enabled) DuplexToggle.IsOn = false;
    }

    // ---------------- scansione (guidata dal browser) ----------------

    private async Task StartScanAsync()
    {
        if (_isScanning) return;

        if (ScannerCombo.SelectedItem is not ScannerInfo scanner)
        {
            SetStatus(InfoBarSeverity.Warning, "Scanner non selezionato",
                "Seleziona uno scanner prima di avviare la scansione.");
            ShowWindow();
            return;
        }

        var options = new ScanOptions
        {
            DeviceId = scanner.DeviceId,
            Dpi = DpiCombo.SelectedItem is int dpi ? dpi : 300,
            ColorMode = (ColorCombo.SelectedItem as ColorOption)?.Value ?? ColorMode.Color,
            Source = (SourceCombo.SelectedItem as SourceOption)?.Value ?? PaperSource.Flatbed,
            Duplex = DuplexToggle.IsOn
        };

        SetScanningState(true);
        SetStatus(InfoBarSeverity.Informational, "Scansione in corso", "Acquisizione dallo scanner…");

        int pageCount = 0;
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
                isCancelled: () => _cancelRequested);

            if (_cancelRequested)
                SetStatus(InfoBarSeverity.Warning, "Interrotta", $"Scansione interrotta dopo {pageCount} pagina/e.");
            else if (pageCount == 0)
                SetStatus(InfoBarSeverity.Warning, "Nessuna pagina", "Lo scanner non ha prodotto immagini.");
            else
                SetStatus(InfoBarSeverity.Success, "Completata", $"{pageCount} pagina/e acquisita/e e inviata/e al browser.");
        }
        catch (Exception ex)
        {
            SetStatus(InfoBarSeverity.Error, "Errore di scansione", ex.Message);
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
        ScannerCombo.IsEnabled = !scanning;
        // Notifica ai browser collegati: occupato/libero, così bloccano altre richieste.
        _server.SetScanning(scanning);
    }

    // ---------------- eventi bridge ----------------

    private void OnClientsChanged(int count)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (count > 0)
                SetStatus(InfoBarSeverity.Success, "Browser collegato",
                    $"{count} pagina/e web in ascolto. Pronto a scansionare.");
            else
                SetStatus(InfoBarSeverity.Informational, "In attesa", "Nessun browser collegato.");
        });

    private void OnScanRequested()
        => DispatcherQueue.TryEnqueue(() =>
        {
            // Scanner già configurato: scansione in background, senza aprire la finestra
            // (si lavora esclusivamente dal browser).
            if (ScannerCombo.SelectedItem is ScannerInfo)
            {
                if (!_isScanning) _ = StartScanAsync();
            }
            else
            {
                // Prima configurazione: mostra la finestra per selezionare lo scanner.
                ShowWindow();
                SetStatus(InfoBarSeverity.Warning, "Scanner non selezionato",
                    "Seleziona uno scanner prima di avviare la scansione dal browser.");
            }
        });

    private void OnStopRequested()
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isScanning) _cancelRequested = true;
        });

    // ---------------- tray / finestra ----------------

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_reallyExit) return;
        // La X minimizza nella tray invece di chiudere (come l'app originale).
        args.Cancel = true;
        _appWindow.Hide();
    }

    private void ShowWindow()
    {
        _appWindow.Show();
        if (_appWindow.Presenter is OverlappedPresenter p) p.Restore();
        _appWindow.MoveInZOrderAtTop();
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _server.Dispose();
        TrayIcon.Dispose();
        Close();
    }

    // ---------------- autostart ----------------

    private void AutostartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            AutostartService.SetEnabled(AutostartToggle.IsOn);
        }
        catch (Exception ex)
        {
            SetStatus(InfoBarSeverity.Error, "Autostart", ex.Message);
        }
    }

    // ---------------- helper UI ----------------

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
