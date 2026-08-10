namespace NewScan.Models;

/// <summary>Scanner WIA individuato sul sistema.</summary>
public sealed record ScannerInfo(string DeviceId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Capacità dichiarate dal device per DPI e modalità colore (lette da WIA_IPS_XRES / WIA_IPA_DATATYPE).</summary>
public sealed record ScannerCapabilities(IReadOnlyList<int> Dpis, IReadOnlyList<ColorMode> ColorModes);

/// <summary>Modalita' colore di acquisizione (mappata sul WIA_IPA_DATATYPE).</summary>
public enum ColorMode
{
    Color,
    Grayscale,
    BlackWhite
}

/// <summary>Origine carta.</summary>
public enum PaperSource
{
    Flatbed,
    Feeder
}

/// <summary>Opzioni di scansione passate al WiaScannerService.</summary>
public sealed class ScanOptions
{
    public string DeviceId { get; init; } = string.Empty;
    public int Dpi { get; init; } = 300;
    public ColorMode ColorMode { get; init; } = ColorMode.Color;
    public PaperSource Source { get; init; } = PaperSource.Flatbed;
    public bool Duplex { get; init; }
}
