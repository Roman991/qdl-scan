using QdlScan.Services;
using Xunit;

namespace QdlScan.Tests;

public class OriginPolicyTests
{
    private static readonly string[] None = Array.Empty<string>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("NULL")]
    public void AcceptsMissingOrNullOrigin(string? origin)
        => Assert.True(OriginPolicy.IsAllowed(origin, None));

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://127.0.0.1")]
    public void AcceptsLocalHostsOnAnyPort(string origin)
        => Assert.True(OriginPolicy.IsAllowed(origin, None));

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://attacker.test")]
    // Trucco del sottodominio: l'host è "localhost.evil.com", non "localhost".
    [InlineData("https://localhost.evil.com")]
    public void RejectsRemoteOrigins(string origin)
        => Assert.False(OriginPolicy.IsAllowed(origin, None));

    [Fact]
    public void AcceptsExplicitAllowlistedOrigin()
        => Assert.True(OriginPolicy.IsAllowed("https://app.miosito.it",
            new[] { "https://app.miosito.it" }));

    [Fact]
    public void AllowlistMatchIsCaseInsensitive()
        => Assert.True(OriginPolicy.IsAllowed("https://App.Miosito.IT",
            new[] { "https://app.miosito.it" }));

    [Fact]
    public void WildcardAllowsAnyOrigin()
        => Assert.True(OriginPolicy.IsAllowed("https://anything.example", new[] { "*" }));
}
