namespace NewScan.Services;

/// <summary>
/// Politica di accettazione dell'header <c>Origin</c> per il bridge WebSocket, estratta in
/// una classe pura (senza dipendenze da Fleck) così da poterla coprire con unit test.
///
/// Blocca il <i>Cross-Site WebSocket Hijacking</i>: un sito remoto ostile invia il proprio
/// Origin e viene rifiutato.
/// </summary>
public static class OriginPolicy
{
    /// <summary>
    /// Decide se accettare una connessione con il dato <paramref name="origin"/>. Accetta:
    /// <list type="bullet">
    ///   <item>Origin assente o "null": contesti <c>file://</c>/sandbox. NB: anche client
    ///   non-browser (curl, altri processi locali) non inviano Origin e vengono accettati —
    ///   limite noto, mitigato dal bind su loopback.</item>
    ///   <item>host locale su qualunque porta (<c>localhost</c>/<c>127.0.0.1</c>/<c>::1</c>);</item>
    ///   <item>origine in <paramref name="allowedOrigins"/> (confronto case-insensitive) o "*".</item>
    /// </list>
    /// </summary>
    public static bool IsAllowed(string? origin, IReadOnlyCollection<string> allowedOrigins)
    {
        if (string.IsNullOrEmpty(origin) || origin.Equals("null", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var allowed in allowedOrigins)
        {
            if (allowed == "*" || string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Accetta qualunque porta su host locale (dev server: localhost:3000, ecc.).
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host == "127.0.0.1" ||
                host == "::1")
                return true;
        }

        return false;
    }
}
