using NewScan.Models;
using NewScan.Services;
using Xunit;

namespace NewScan.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultsAreSensible()
    {
        var s = new AppSettings();

        Assert.Equal(8787, s.Port);
        Assert.Equal(300, s.DefaultDpi);
        Assert.Equal(ColorMode.Color, s.DefaultColorMode);
        Assert.Equal(new[] { "https://app.quellideilibri.it" }, s.AllowedOrigins);
    }
}
