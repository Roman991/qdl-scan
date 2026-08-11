using System;
using System.Threading;
using System.Windows.Forms;

namespace QdlScan;

internal static class Program
{
    private const string MutexName = "QdlScan-SingleInstance-Mutex";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "Qdl Scan è già in esecuzione.",
                "Qdl Scan",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
