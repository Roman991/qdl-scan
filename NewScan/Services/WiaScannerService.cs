using System.Runtime.InteropServices;
using System.Threading;
using NewScan.Models;

namespace NewScan.Services;

/// <summary>
/// Acquisizione documenti tramite WIA (Windows Image Acquisition), in sostituzione
/// del precedente layer TWAIN/NTwain.
///
/// Usa late binding COM (<c>dynamic</c> + ProgID "WIA.DeviceManager") così da non
/// dipendere dalla generazione di un assembly di interop e dal GUID della type
/// library. Tutte le chiamate COM girano su un thread STA dedicato.
/// </summary>
public sealed class WiaScannerService
{
    // --- WIA property id ---
    private const int WIA_IPS_XRES = 6147;
    private const int WIA_IPS_YRES = 6148;
    private const int WIA_IPA_DATATYPE = 4103;
    private const int WIA_IPS_DOCUMENT_HANDLING_SELECT = 3088;
    private const int WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES = 3086;
    private const int WIA_DPS_DOCUMENT_HANDLING_STATUS = 3087;

    // --- WIA_IPA_DATATYPE values ---
    private const int WIA_DATA_THRESHOLD = 0; // bianco/nero
    private const int WIA_DATA_GRAYSCALE = 2;
    private const int WIA_DATA_COLOR = 3;

    // --- Property.SubType (WIA Automation Layer) ---
    private const int WIA_PROP_RANGE = 1;
    private const int WIA_PROP_LIST = 2;

    // DPI "standard" da usare quando il device espone XRES come range continuo
    // (min/max/step) invece di un elenco discreto di valori supportati.
    private static readonly int[] CommonDpiCandidates = { 75, 100, 150, 200, 300, 400, 600, 1200, 2400, 4800 };

    // --- DOCUMENT_HANDLING_SELECT / CAPABILITIES bit ---
    private const int FEEDER = 0x001;
    private const int FLATBED = 0x002;
    private const int DUPLEX = 0x004;

    // --- DOCUMENT_HANDLING_STATUS bit ---
    private const int FEED_READY = 0x001;

    // WiaDeviceType.ScannerDeviceType
    private const int ScannerDeviceType = 1;

    private const string WiaFormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    // WIA_ERROR_PAPER_EMPTY / sheet-fed esaurito
    private const int WIA_ERROR_PAPER_EMPTY = unchecked((int)0x80210003);

    /// <summary>Enumera gli scanner WIA installati.</summary>
    public Task<IReadOnlyList<ScannerInfo>> GetScannersAsync()
        => RunStaAsync(() =>
        {
            var result = new List<ScannerInfo>();
            dynamic? mgr = null;
            try
            {
                mgr = CreateDeviceManager();
                dynamic infos = mgr.DeviceInfos;
                int count = infos.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic info = infos[i];
                    try
                    {
                        if ((int)info.Type != ScannerDeviceType) continue;
                        string id = info.DeviceID;
                        string name = GetStringProperty(info.Properties, "Name") ?? id;
                        result.Add(new ScannerInfo(id, name));
                    }
                    finally { Release(info); }
                }
            }
            finally { Release(mgr); }
            return (IReadOnlyList<ScannerInfo>)result;
        });

    /// <summary>Indica se il device dichiara il supporto fronte/retro (duplex).</summary>
    public Task<bool> SupportsDuplexAsync(string deviceId)
        => RunStaAsync(() =>
        {
            dynamic? device = null;
            try
            {
                device = ConnectDevice(deviceId);
                if (device is null) return false;
                int caps = GetIntProperty(device.Properties, WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES, 0);
                return (caps & DUPLEX) != 0;
            }
            catch { return false; }
            finally { Release(device); }
        });

    /// <summary>
    /// Legge dal device i DPI e le modalità colore effettivamente supportati
    /// (proprietà <c>WIA_IPS_XRES</c> e <c>WIA_IPA_DATATYPE</c> dell'item), così
    /// da poter popolare le combo della UI con valori realmente accettati dallo
    /// scanner invece di un elenco fisso.
    /// </summary>
    public Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId)
        => RunStaAsync(() =>
        {
            dynamic? device = null;
            dynamic? item = null;
            try
            {
                device = ConnectDevice(deviceId)
                         ?? throw new InvalidOperationException("Scanner non trovato o non disponibile.");
                item = device.Items[1];

                List<int> dpis = ReadIntCapability(item.Properties, WIA_IPS_XRES);
                List<int> rawColorValues = ReadIntCapability(item.Properties, WIA_IPA_DATATYPE);

                var colorModes = rawColorValues
                    .Select(ToColorMode)
                    .Where(m => m.HasValue)
                    .Select(m => m!.Value)
                    .Distinct()
                    .ToList();

                return new ScannerCapabilities(dpis, colorModes);
            }
            finally
            {
                Release(item);
                Release(device);
            }
        });

    /// <summary>
    /// Legge i valori interi ammessi per una proprietà WIA: elenco discreto per le
    /// proprietà di tipo <c>WIA_PROP_LIST</c>, altrimenti (tipicamente
    /// <c>WIA_PROP_RANGE</c>) i candidati "standard" compatibili con min/max/step
    /// dichiarati dal device. In caso di proprietà non leggibile ritorna una lista
    /// vuota: il chiamante deciderà il fallback.
    /// </summary>
    private static List<int> ReadIntCapability(dynamic properties, int propId)
    {
        var values = new List<int>();
        try
        {
            dynamic prop = properties[propId];
            int subType = (int)prop.SubType;

            if (subType == WIA_PROP_LIST)
            {
                dynamic list = prop.SubTypeValues;
                int count = list.Count;
                for (int i = 1; i <= count; i++)
                    values.Add((int)list[i]);
            }
            else if (subType == WIA_PROP_RANGE)
            {
                int min = (int)prop.SubTypeMin;
                int max = (int)prop.SubTypeMax;
                int step = (int)prop.SubTypeStep;
                if (step <= 0) step = 1;

                foreach (int candidate in CommonDpiCandidates)
                {
                    if (candidate < min || candidate > max) continue;
                    if ((candidate - min) % step == 0) values.Add(candidate);
                }

                if (values.Count == 0) values.Add((int)prop.Value);
            }
            else
            {
                // WIA_PROP_NONE / WIA_PROP_FLAG: nessun elenco enumerabile, usa il valore corrente.
                values.Add((int)prop.Value);
            }
        }
        catch
        {
            // capability non leggibile: elenco vuoto, il chiamante userà un fallback
        }
        return values;
    }

    private static ColorMode? ToColorMode(int value) => value switch
    {
        WIA_DATA_COLOR => ColorMode.Color,
        WIA_DATA_GRAYSCALE => ColorMode.Grayscale,
        WIA_DATA_THRESHOLD => ColorMode.BlackWhite,
        _ => null
    };

    /// <summary>
    /// Acquisisce una o più pagine. Per ogni pagina invoca <paramref name="onPage"/>
    /// con i byte JPEG. In modalità alimentatore (ADF) prosegue finché ci sono fogli
    /// o finché <paramref name="isCancelled"/> ritorna true.
    /// </summary>
    public Task ScanAsync(ScanOptions options, Action<byte[]> onPage, Func<bool> isCancelled)
        => RunStaAsync<object?>(() =>
        {
            dynamic? device = null;
            dynamic? item = null;
            try
            {
                device = ConnectDevice(options.DeviceId)
                         ?? throw new InvalidOperationException("Scanner non trovato o non disponibile.");
                item = device.Items[1];

                ApplySettings(device, item, options);

                bool useFeeder = options.Source == PaperSource.Feeder;
                do
                {
                    if (isCancelled()) break;

                    byte[] bytes;
                    try
                    {
                        dynamic imageFile = item.Transfer(WiaFormatJpeg);
                        try
                        {
                            bytes = (byte[])imageFile.FileData.BinaryData;
                        }
                        finally { Release(imageFile); }
                    }
                    catch (COMException ex) when (ex.HResult == WIA_ERROR_PAPER_EMPTY)
                    {
                        // Alimentatore vuoto: fine acquisizione, non è un errore.
                        break;
                    }

                    if (bytes.Length > 0)
                        onPage(bytes);

                    if (!useFeeder) break;

                    // Continua finché l'alimentatore segnala fogli pronti.
                    int status = GetIntProperty(device.Properties, WIA_DPS_DOCUMENT_HANDLING_STATUS, 0);
                    if ((status & FEED_READY) == 0) break;
                }
                while (!isCancelled());
            }
            finally
            {
                Release(item);
                Release(device);
            }
            return null;
        });

    private void ApplySettings(dynamic device, dynamic item, ScanOptions options)
    {
        // Origine / duplex (best effort: dipende dal device)
        if (options.Source == PaperSource.Feeder)
        {
            int select = FEEDER | (options.Duplex ? DUPLEX : 0);
            TrySetProperty(device.Properties, WIA_IPS_DOCUMENT_HANDLING_SELECT, select);
        }
        else
        {
            TrySetProperty(device.Properties, WIA_IPS_DOCUMENT_HANDLING_SELECT, FLATBED);
        }

        TrySetProperty(item.Properties, WIA_IPS_XRES, options.Dpi);
        TrySetProperty(item.Properties, WIA_IPS_YRES, options.Dpi);
        TrySetProperty(item.Properties, WIA_IPA_DATATYPE, ToDataType(options.ColorMode));
    }

    private static int ToDataType(ColorMode mode) => mode switch
    {
        ColorMode.Color => WIA_DATA_COLOR,
        ColorMode.Grayscale => WIA_DATA_GRAYSCALE,
        ColorMode.BlackWhite => WIA_DATA_THRESHOLD,
        _ => WIA_DATA_COLOR
    };

    // ---------------- COM helpers ----------------

    private static dynamic CreateDeviceManager()
    {
        var type = Type.GetTypeFromProgID("WIA.DeviceManager")
                   ?? throw new InvalidOperationException(
                       "WIA non disponibile su questo sistema (servizio 'Acquisizione immagini di Windows' assente).");
        return Activator.CreateInstance(type)!;
    }

    private static dynamic? ConnectDevice(string deviceId)
    {
        dynamic? mgr = null;
        try
        {
            mgr = CreateDeviceManager();
            dynamic infos = mgr.DeviceInfos;
            int count = infos.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic info = infos[i];
                try
                {
                    string id = info.DeviceID;
                    if (string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))
                        return info.Connect();
                }
                finally { Release(info); }
            }
            return null;
        }
        finally { Release(mgr); }
    }

    private static string? GetStringProperty(dynamic properties, string name)
    {
        try { return (string?)properties[name].Value; }
        catch { return null; }
    }

    private static int GetIntProperty(dynamic properties, int propId, int fallback)
    {
        try { return (int)properties[propId].Value; }
        catch { return fallback; }
    }

    private static void TrySetProperty(dynamic properties, int propId, int value)
    {
        try { properties[propId].Value = value; }
        catch { /* capability non supportata dal device: si ignora */ }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    /// <summary>Esegue un'azione COM su un thread STA dedicato.</summary>
    private static Task<T> RunStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "WIA-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
