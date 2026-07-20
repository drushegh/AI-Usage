using System.Text;
using Xunit;

namespace ClaudeUsageProbe.Tests;

/// <summary>
/// End-to-end acceptance tests for <c>ClaudeUsageProbe.exe</c> (PLAN T24, DESIGN §2(b)/§4.2/§6). Every
/// test launches the REAL executable as a child process against a LOCAL stub server (never the real
/// endpoint) and inspects the actual stdout bytes, stderr text, and exit code — the only honest way to
/// assert token hygiene.
/// </summary>
public sealed class ProbeEndToEndTests
{
    private const string Version = "2.1.191";
    private const string ExpectedUserAgent = "claude-code/2.1.191";

    private static byte[] FixtureBytes()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "raw-capture-1.json"));

    private static ProbeRun RunAgainst(StubServer stub, TempOAuthFile credentials, string version = Version)
        => ProbeProcess.Run(
            new[] { "--claude-version", version, "--credentials", credentials.Path },
            endpointOverride: stub.UsageUrl);

    // ---- 200 success: relays the exact body, sends the right bearer + UA -------------------------

    [Fact]
    public void Success200_RelaysExactBody_SendsBearerAndUserAgent_Exit0()
    {
        using var credentials = new TempOAuthFile(); // ctor applies a deterministic clean ACL => no warning
        using var stub = StubServer.Ok(FixtureBytes());

        var run = RunAgainst(stub, credentials);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(FixtureBytes(), run.StdoutBytes);          // byte-exact verbatim relay
        Assert.Equal(string.Empty, run.Stderr.Trim());          // clean ACL => silent

        Assert.Equal(1, stub.RequestCount);
        Assert.Equal("GET", stub.LastMethod);
        Assert.Equal(AnthropicUsageEndpoint.Path, stub.LastPath);
        Assert.Equal("Bearer " + credentials.AccessToken, stub.LastAuthorization);
        Assert.Equal(ExpectedUserAgent, stub.LastUserAgent);    // UA is EXACTLY claude-code/<ver>

        AssertNoTokenLeak(run, credentials);
    }

    // ---- Distinct exit codes per HTTP outcome ---------------------------------------------------

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void AuthRejected_Exit4(int status)
    {
        using var credentials = new TempOAuthFile();
        using var stub = StubServer.Status(status);

        var run = RunAgainst(stub, credentials);

        Assert.Equal(4, run.ExitCode);
        Assert.Equal("auth-rejected", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        AssertNoTokenLeak(run, credentials);
    }

    [Fact]
    public void Throttled429_Exit5()
    {
        using var credentials = new TempOAuthFile();
        using var stub = StubServer.Status(429);

        var run = RunAgainst(stub, credentials);

        Assert.Equal(5, run.ExitCode);
        Assert.Equal("throttled", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        AssertNoTokenLeak(run, credentials);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    public void Redirect_NotFollowed_Exit6(int status)
    {
        using var credentials = new TempOAuthFile();
        // A redirect to another loopback path: if the probe (wrongly) followed it, the stub would see 2
        // requests. AllowAutoRedirect=false means it must see exactly ONE.
        using var stub = StubServer.Redirect(status, "http://127.0.0.1:9/elsewhere");

        var run = RunAgainst(stub, credentials);

        Assert.Equal(6, run.ExitCode);
        Assert.Equal("redirect-blocked", run.Stderr.Trim());
        Assert.Equal(1, stub.RequestCount);   // the bearer was NOT carried to the redirect target
        Assert.Empty(run.StdoutBytes);
        AssertNoTokenLeak(run, credentials);
    }

    [Fact]
    public void NonJson200_Exit7_DoesNotRelayBody()
    {
        using var credentials = new TempOAuthFile();
        using var stub = StubServer.OkRaw(StubServer.Html("<html>not json</html>"), "text/html");

        var run = RunAgainst(stub, credentials);

        Assert.Equal(7, run.ExitCode);
        Assert.Equal("schema", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);        // an HTML error page is never relayed to stdout
        AssertNoTokenLeak(run, credentials);
    }

    [Fact]
    public void Empty200_Exit7()
    {
        using var credentials = new TempOAuthFile();
        using var stub = StubServer.Status(200); // 200 with an empty body

        var run = RunAgainst(stub, credentials);

        Assert.Equal(7, run.ExitCode);
        Assert.Equal("schema", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
    }

    [Fact]
    public void UnexpectedStatus500_Exit6_WithNonSensitiveStatusCode()
    {
        using var credentials = new TempOAuthFile();
        using var stub = StubServer.Status(500);

        var run = RunAgainst(stub, credentials);

        Assert.Equal(6, run.ExitCode);
        Assert.Equal("http-status:500", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        AssertNoTokenLeak(run, credentials);
    }

    // ---- Argument / credential failure paths (no network) ---------------------------------------

    [Fact]
    public void MissingVersionArg_Exit2()
    {
        using var credentials = new TempOAuthFile();
        using var stub = StubServer.Ok(FixtureBytes());

        var run = ProbeProcess.Run(
            new[] { "--credentials", credentials.Path }, endpointOverride: stub.UsageUrl);

        Assert.Equal(2, run.ExitCode);
        Assert.Equal("usage", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        Assert.Equal(0, stub.RequestCount);   // never reached the network
    }

    [Fact]
    public void MissingCredentialsFile_Exit3_NoNetworkCall()
    {
        using var stub = StubServer.Ok(FixtureBytes());
        var missing = Path.Combine(Path.GetTempPath(), "cup-does-not-exist-" + Guid.NewGuid().ToString("N"), ".credentials.json");

        var run = ProbeProcess.Run(
            new[] { "--claude-version", Version, "--credentials", missing }, endpointOverride: stub.UsageUrl);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("credentials-missing", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        Assert.Equal(0, stub.RequestCount);   // credentials are checked before any request is made
    }

    [Fact]
    public void MalformedCredentials_NoAccessToken_Exit3()
    {
        using var credentials = new TempOAuthFile();
        File.WriteAllText(credentials.Path, "{\"claudeAiOauth\":{\"refreshToken\":\"only-refresh\"}}");
        using var stub = StubServer.Ok(FixtureBytes());

        var run = RunAgainst(stub, credentials);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("credentials-invalid", run.Stderr.Trim());
        Assert.Equal(0, stub.RequestCount);
    }

    // ---- Endpoint override security gate --------------------------------------------------------

    [Fact]
    public void NonLoopbackEndpointOverride_Refused_Exit2_BeforeReadingToken()
    {
        using var credentials = new TempOAuthFile();

        var run = ProbeProcess.Run(
            new[] { "--claude-version", Version, "--credentials", credentials.Path },
            endpointOverride: "http://93.184.216.34/api/oauth/usage"); // non-loopback

        Assert.Equal(2, run.ExitCode);
        Assert.Equal("usage:endpoint-override-not-loopback", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        AssertNoTokenLeak(run, credentials);
    }

    // ---- ACL audit: warn-and-proceed ------------------------------------------------------------

    [Fact]
    public void CleanAcl_NoWarning_Exit0()
    {
        using var credentials = new TempOAuthFile(); // ctor applies a clean protected ACL
        using var stub = StubServer.Ok(FixtureBytes());

        var run = RunAgainst(stub, credentials);

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("acl", run.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermissiveAcl_WarnsButProceeds_Exit0()
    {
        using var credentials = new TempOAuthFile();
        credentials.AddEveryoneReadAce();
        using var stub = StubServer.Ok(FixtureBytes());

        var run = RunAgainst(stub, credentials);

        Assert.Equal(0, run.ExitCode);                          // proceeded, never refused
        Assert.Contains("WARN acl-permissive:everyone", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(FixtureBytes(), run.StdoutBytes);          // and still relayed the body
        AssertNoTokenLeak(run, credentials);                    // the warning carries no token
    }

    // ---- Refresh token is never read, sent, or leaked -------------------------------------------

    [Fact]
    public void RefreshToken_NeverSent_NeverLeaked()
    {
        using var credentials = new TempOAuthFile();
        using var stub = StubServer.Ok(FixtureBytes());

        var run = RunAgainst(stub, credentials);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Bearer " + credentials.AccessToken, stub.LastAuthorization);
        Assert.DoesNotContain(credentials.RefreshToken, stub.LastAuthorization ?? string.Empty, StringComparison.Ordinal);
        AssertNoTokenLeak(run, credentials);
    }

    // ---- Security hardening: token never relayed, bounded reads, header validation --------------

    [Fact]
    public void Response200_EchoingBearerToken_IsNeverRelayed_Exit7()
    {
        // A (hypothetical) endpoint bug/compromise that echoes the bearer back in the JSON body must
        // never reach stdout — the last-gate byte scan fails closed to exit 7 with empty stdout.
        using var credentials = new TempOAuthFile();
        var body = Encoding.UTF8.GetBytes("{\"leaked\":\"" + credentials.AccessToken + "\"}");
        using var stub = StubServer.Ok(body);

        var run = RunAgainst(stub, credentials);

        Assert.Equal(7, run.ExitCode);
        Assert.Equal("token-in-response", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);            // the echoed token is NEVER relayed
        AssertNoTokenLeak(run, credentials);
    }

    [Fact]
    public void RefreshTokenBeforeAccessToken_StillExtractsOnlyAccessToken_Exit0()
    {
        // refreshToken precedes accessToken in source order: the streaming reader skips it and still
        // sends exactly the access token — the refresh token is never bound, sent, or leaked.
        using var credentials = new TempOAuthFile();
        File.WriteAllText(
            credentials.Path,
            "{\"claudeAiOauth\":{\"refreshToken\":\"" + credentials.RefreshToken +
            "\",\"accessToken\":\"" + credentials.AccessToken + "\"}}");
        using var stub = StubServer.Ok(FixtureBytes());

        var run = RunAgainst(stub, credentials);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Bearer " + credentials.AccessToken, stub.LastAuthorization);
        AssertNoTokenLeak(run, credentials);
    }

    [Fact]
    public void AccessTokenWithControlChar_FailsClosed_NothingSent_Exit3()
    {
        // A tampered credentials file whose accessToken contains a newline must fail closed BEFORE any
        // request is built — nothing is ever sent (header-splitting injection is impossible).
        using var credentials = new TempOAuthFile();
        File.WriteAllText(credentials.Path, "{\"claudeAiOauth\":{\"accessToken\":\"abc\\ndef\"}}");
        using var stub = StubServer.Ok(FixtureBytes());

        var run = RunAgainst(stub, credentials);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("token-malformed", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        Assert.Equal(0, stub.RequestCount);       // never reached the network
    }

    [Fact]
    public void OversizedResponse_DeclaredContentLength_Capped_Exit7_NoStdout()
    {
        using var credentials = new TempOAuthFile();
        var big = Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('a', 300 * 1024) + "\"}");
        using var stub = StubServer.Ok(big);      // Content-Length declared > 256 KiB cap

        var run = RunAgainst(stub, credentials);

        Assert.Equal(7, run.ExitCode);
        Assert.Equal("response-too-large", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);            // an oversized body is never buffered or relayed
        AssertNoTokenLeak(run, credentials);
    }

    [Fact]
    public void OversizedResponse_ChunkedStream_Capped_Exit7_NoStdout()
    {
        using var credentials = new TempOAuthFile();
        var big = Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('a', 300 * 1024) + "\"}");
        using var stub = StubServer.OkChunked(big);   // no Content-Length → streaming cap must fire

        var run = RunAgainst(stub, credentials);

        Assert.Equal(7, run.ExitCode);
        Assert.Equal("response-too-large", run.Stderr.Trim());
        Assert.Empty(run.StdoutBytes);
        AssertNoTokenLeak(run, credentials);
    }

    // ---- Token-leak sweep across every failure mode ---------------------------------------------

    [Theory]
    [InlineData("401")]
    [InlineData("429")]
    [InlineData("302")]
    [InlineData("nonjson")]
    [InlineData("500")]
    public void FailureModes_NeverLeakTokenToStdoutOrStderr(string scenario)
    {
        using var credentials = new TempOAuthFile();
        using var stub = MakeStub(scenario);

        var run = RunAgainst(stub, credentials);

        Assert.NotEqual(0, run.ExitCode);
        AssertNoTokenLeak(run, credentials);
    }

    private static StubServer MakeStub(string scenario) => scenario switch
    {
        "401" => StubServer.Status(401),
        "429" => StubServer.Status(429),
        "302" => StubServer.Redirect(302, "http://127.0.0.1:9/x"),
        "nonjson" => StubServer.OkRaw(StubServer.Html("<html>err</html>"), "text/html"),
        "500" => StubServer.Status(500),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "unknown scenario"),
    };

    // ---- helpers --------------------------------------------------------------------------------

    private static void AssertNoTokenLeak(ProbeRun run, TempOAuthFile credentials)
    {
        Assert.DoesNotContain(credentials.AccessToken, run.StdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.AccessToken, run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.RefreshToken, run.StdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.RefreshToken, run.Stderr, StringComparison.Ordinal);

        // Belt and suspenders: the token must not appear in the raw stdout bytes either.
        Assert.False(ContainsSubsequence(run.StdoutBytes, Encoding.UTF8.GetBytes(credentials.AccessToken)));
        Assert.False(ContainsSubsequence(run.StdoutBytes, Encoding.UTF8.GetBytes(credentials.RefreshToken)));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
