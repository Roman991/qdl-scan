using Microsoft.UI.Xaml;

namespace NewScan;

/// <summary>
/// Entry point dell'applicazione WinUI 3 (sostituisce il vecchio Program.cs WinForms).
/// L'app parte minimizzata nella tray: la finestra viene creata ma non attivata.
/// </summary>
public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        // Avvio in background: la finestra resta nascosta nella tray finche'
        // l'utente non la apre o il browser non richiede una scansione.
        Window.StartHidden();
    }
}
