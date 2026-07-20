using Xunit;

namespace ClaudeUsageProbe.Tests;

/// <summary>
/// In-process unit tests for the probe's pure helpers (reached via <c>InternalsVisibleTo</c>). These
/// cover the input-validation and endpoint-gating logic without spawning a process — the end-to-end
/// tests then confirm the same behaviour in the real executable.
/// </summary>
public sealed class ProbeUnitTests
{
    // ---- ProbeOptions parsing -------------------------------------------------------------------

    [Fact]
    public void TryParse_ValidArgs_ExtractsVersionAndCredentialsPath()
    {
        var ok = ProbeOptions.TryParse(
            new[] { "--claude-version", "2.1.191", "--credentials", @"C:\x\.credentials.json" },
            out var options, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("2.1.191", options!.ClaudeVersion);
        Assert.Equal(@"C:\x\.credentials.json", options.CredentialsPath);
    }

    [Fact]
    public void TryParse_NoCredentialsArg_DefaultsToUserProfilePath()
    {
        var ok = ProbeOptions.TryParse(new[] { "--claude-version", "2.1.191" }, out var options, out _);

        Assert.True(ok);
        Assert.NotNull(options);
        Assert.EndsWith(Path.Combine(".claude", ".credentials.json"), options!.CredentialsPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_MissingVersion_IsUsageError()
    {
        var ok = ProbeOptions.TryParse(Array.Empty<string>(), out var options, out var error);

        Assert.False(ok);
        Assert.Null(options);
        Assert.Equal(ReasonCodes.Usage, error);
    }

    [Fact]
    public void TryParse_VersionFlagWithoutValue_IsUsageError()
    {
        var ok = ProbeOptions.TryParse(new[] { "--claude-version" }, out _, out var error);

        Assert.False(ok);
        Assert.Equal(ReasonCodes.Usage, error);
    }

    [Fact]
    public void TryParse_UnknownArg_IsUsageError()
    {
        var ok = ProbeOptions.TryParse(new[] { "--claude-version", "2.1.191", "--boom" }, out _, out var error);

        Assert.False(ok);
        Assert.Equal(ReasonCodes.Usage, error);
    }

    [Theory]
    [InlineData("1.0\n2.0")]   // LF — header injection attempt
    [InlineData("1.0\r\nX")]   // CRLF
    [InlineData("1.0\tbeta")]  // tab (control char)
    [InlineData("1.0\0")]      // NUL
    public void TryParse_ControlCharsInVersion_IsBadVersion(string version)
    {
        var ok = ProbeOptions.TryParse(new[] { "--claude-version", version }, out _, out var error);

        Assert.False(ok);
        Assert.Equal(ReasonCodes.BadVersion, error);
    }

    [Fact]
    public void IsValidVersion_RejectsOverlongAndEmpty_AcceptsSemver()
    {
        Assert.True(ProbeOptions.IsValidVersion("2.1.191"));
        Assert.True(ProbeOptions.IsValidVersion("2.1.191-native.abcdef"));
        Assert.False(ProbeOptions.IsValidVersion(string.Empty));
        Assert.False(ProbeOptions.IsValidVersion(new string('9', 200)));
    }

    // ---- Endpoint resolution + loopback gate ----------------------------------------------------

    [Fact]
    public void Resolve_NoOverride_UsesHardCodedAnthropicEndpoint()
    {
        var (endpoint, error) = AnthropicUsageEndpoint.Resolve(overrideValue: null);

        Assert.Null(error);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", endpoint.ToString());
    }

    [Theory]
    [InlineData("http://127.0.0.1:5005/api/oauth/usage")]
    [InlineData("http://localhost:5005/api/oauth/usage")]
    [InlineData("http://[::1]:5005/api/oauth/usage")]
    public void Resolve_LoopbackOverride_IsAccepted(string url)
    {
        var (endpoint, error) = AnthropicUsageEndpoint.Resolve(url);

        Assert.Null(error);
        Assert.True(endpoint.IsLoopback);
    }

    [Theory]
    [InlineData("http://93.184.216.34/api/oauth/usage")]  // public IP
    [InlineData("https://evil.example.com/api/oauth/usage")]
    [InlineData("ftp://127.0.0.1/x")]                      // wrong scheme
    [InlineData("not-a-uri")]
    public void Resolve_NonLoopbackOrBadOverride_IsRefused(string url)
    {
        var (_, error) = AnthropicUsageEndpoint.Resolve(url);

        Assert.Equal(ReasonCodes.EndpointOverrideNotLoopback, error);
    }

    // ---- AccessTokenSource: bounded, single-handle read (never materialises the refresh token) --

    [Fact]
    public void TryLoadAccessToken_RefreshBeforeAccess_ExtractsOnlyAccessToken()
    {
        var dir = Directory.CreateTempSubdirectory("cup-order-").FullName;
        try
        {
            var path = Path.Combine(dir, ".credentials.json");
            File.WriteAllText(path, "{\"claudeAiOauth\":{\"refreshToken\":\"REFRESH-XYZ\",\"accessToken\":\"ACCESS-ABC\"}}");

            var ok = AccessTokenSource.TryLoadAccessToken(path, out var token, out var error, out _);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal("ACCESS-ABC", token);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoadAccessToken_OversizeFile_Rejected_Exit3()
    {
        var dir = Directory.CreateTempSubdirectory("cup-oversize-").FullName;
        try
        {
            var path = Path.Combine(dir, ".credentials.json");
            // > 64 KiB. Valid JSON, but the handle-based size gate rejects it before any parse.
            File.WriteAllText(path, "{\"claudeAiOauth\":{\"accessToken\":\"" + new string('a', 70 * 1024) + "\"}}");

            var ok = AccessTokenSource.TryLoadAccessToken(path, out var token, out var error, out _);

            Assert.False(ok);
            Assert.Equal(string.Empty, token);
            Assert.Equal(ReasonCodes.CredentialsUnreadable, error);   // maps to exit 3 (Credentials)
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoadAccessToken_ReparseOrNonRegular_Rejected()
    {
        var dir = Directory.CreateTempSubdirectory("cup-reparse-").FullName;
        try
        {
            var real = Path.Combine(dir, ".credentials.json");
            File.WriteAllText(real, "{\"claudeAiOauth\":{\"accessToken\":\"ACCESS-ABC\"}}");

            var link = Path.Combine(dir, "link.credentials.json");
            if (TryCreateFileSymlink(link, real))
            {
                // Reparse point at the credentials path is rejected without being followed.
                var ok = AccessTokenSource.TryLoadAccessToken(link, out _, out var error, out _);
                Assert.False(ok);
                Assert.Equal(ReasonCodes.CredentialsUnreadable, error);
            }
            else
            {
                // No symlink privilege here: exercise the non-regular reject deterministically instead —
                // a directory is not a readable credentials file, so the load still fails (exit 3).
                var ok = AccessTokenSource.TryLoadAccessToken(dir, out _, out var error, out _);
                Assert.False(ok);
                Assert.True(error == ReasonCodes.CredentialsMissing || error == ReasonCodes.CredentialsUnreadable);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return File.Exists(link);
        }
        catch
        {
            return false; // creating a symlink needs privilege/Developer Mode; fall back to a directory
        }
    }

    // ---- UsageProbeClient: bearer-sink host assertion (defence in depth) -------------------------

    [Theory]
    [InlineData("https://evil.example.com/api/oauth/usage")]            // wrong host
    [InlineData("http://api.anthropic.com/api/oauth/usage")]            // not https, not loopback
    [InlineData("https://api.anthropic.com:8443/api/oauth/usage")]      // wrong port
    [InlineData("https://api.anthropic.com/wrong/path")]                // wrong path
    [InlineData("https://user:pass@api.anthropic.com/api/oauth/usage")] // userinfo present
    public async Task ExecuteAsync_DisallowedTokenSink_FailsClosed_NoSend(string url)
    {
        var result = await UsageProbeClient.ExecuteAsync(
            new Uri(url), "SENTINEL-token-value", "2.1.191", CancellationToken.None);

        Assert.Equal(ExitCodes.Transport, result.ExitCode);
        Assert.Equal(ReasonCodes.EndpointNotAllowed, result.Reason);
        Assert.Null(result.BodyToStdout);
    }

    [Fact]
    public void IsTokenSinkAllowed_AcceptsRealEndpointAndLoopback_RejectsOthers()
    {
        Assert.True(UsageProbeClient.IsTokenSinkAllowed(new Uri("https://api.anthropic.com/api/oauth/usage")));
        Assert.True(UsageProbeClient.IsTokenSinkAllowed(new Uri("http://127.0.0.1:5005/api/oauth/usage")));
        Assert.False(UsageProbeClient.IsTokenSinkAllowed(new Uri("https://evil.example.com/api/oauth/usage")));
        Assert.False(UsageProbeClient.IsTokenSinkAllowed(new Uri("https://user:pass@api.anthropic.com/api/oauth/usage")));
    }

    // ---- UsageProbeClient: token header validation (fail closed, nothing sent) -------------------

    [Theory]
    [InlineData("tok\nen")]       // newline
    [InlineData("tok en")]        // space
    [InlineData("tok\ten")]       // tab
    [InlineData("tok\u0000en")]   // NUL
    [InlineData("")]              // empty
    public async Task ExecuteAsync_MalformedToken_FailsClosed_NothingSent(string token)
    {
        // A loopback sink is used so that IF the guard were bypassed a request WOULD be attempted; because
        // the token is rejected first, nothing is sent and no server is needed (the port is never dialled).
        var result = await UsageProbeClient.ExecuteAsync(
            new Uri("http://127.0.0.1:1/api/oauth/usage"), token, "2.1.191", CancellationToken.None);

        Assert.Equal(ExitCodes.Credentials, result.ExitCode);
        Assert.Equal(ReasonCodes.TokenMalformed, result.Reason);
        Assert.Null(result.BodyToStdout);
    }

    [Fact]
    public void TryBuildBearerHeader_AcceptsCompactToken_RejectsControlAndWhitespace()
    {
        Assert.True(UsageProbeClient.TryBuildBearerHeader("sk-ant-abc._-123", out var header) && header is not null);
        Assert.Equal("Bearer sk-ant-abc._-123", header!.ToString());

        Assert.False(UsageProbeClient.TryBuildBearerHeader("has space", out _));
        Assert.False(UsageProbeClient.TryBuildBearerHeader("has\nnewline", out _));
        Assert.False(UsageProbeClient.TryBuildBearerHeader(string.Empty, out _));
    }
}
