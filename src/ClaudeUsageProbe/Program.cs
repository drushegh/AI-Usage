namespace ClaudeUsageProbe;

/// <summary>
/// <c>ClaudeUsageProbe.exe</c> — the short-lived, credential-touching process of the AI-Usage tray
/// (DESIGN §2(b), §4.2, §6). It reads the OAuth access token from <c>~/.claude/.credentials.json</c>,
/// makes exactly ONE request to <c>GET https://api.anthropic.com/api/oauth/usage</c> with the
/// argv-supplied version in the <c>claude-code/&lt;ver&gt;</c> User-Agent, relays the validated usage
/// JSON to stdout, and exits. The token is erased by process exit — the honest mechanism.
///
/// <para>Token hygiene (the whole point): the token appears in NO argv, NO environment variable, NO
/// stdout (stdout is the usage JSON only), NO stderr, NO log, and NO exception message. On any failure
/// only a short, token-free reason CODE is written to stderr.</para>
///
/// <para>Usage: <c>ClaudeUsageProbe --claude-version &lt;ver&gt; [--credentials &lt;path&gt;]</c></para>
///
/// <para>Exit codes (see <see cref="ExitCodes"/>): 0 ok · 2 usage/args · 3 credentials missing/unreadable
/// · 4 auth-rejected (401/403) · 5 throttled (429) · 6 transport/timeout/3xx · 7 schema-invalid.</para>
///
/// <para>Deployment (DESIGN §6, PLAN T42): launch by absolute install-dir path, no shell, at most one in
/// flight, with the environment NOT setting <c>DOTNET_DbgEnableMiniDump</c>, and with no WER LocalDumps
/// registry key for this exe. See <see cref="CrashHardening"/>.</para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Reduce the chance a crash ever persists the in-memory token. Never fatal.
        CrashHardening.HardenBestEffort();

        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Absolute backstop: no exception may escape to the runtime's default crash printer, which
            // would write exception text to stderr. Only a fixed, token-free code is ever emitted.
            WriteReason(ReasonCodes.Unexpected);
            return ExitCodes.Transport;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (!ProbeOptions.TryParse(args, out var options, out var argError) || options is null)
        {
            WriteReason(argError ?? ReasonCodes.Usage);
            return ExitCodes.Usage;
        }

        var (endpoint, endpointError) = AnthropicUsageEndpoint.Resolve();
        if (endpointError is not null)
        {
            WriteReason(endpointError);
            return ExitCodes.Usage;
        }

        if (!AccessTokenSource.TryLoadAccessToken(
                options.CredentialsPath, out var token, out var credentialError, out var aclWarnings))
        {
            WriteReason(credentialError ?? ReasonCodes.CredentialsUnreadable);
            return ExitCodes.Credentials;
        }

        // Warn-and-proceed: ACL findings are surfaced but never block the read.
        foreach (var warning in aclWarnings)
        {
            WriteReason("WARN " + warning);
        }

        var result = await UsageProbeClient
            .ExecuteAsync(endpoint, token, options.ClaudeVersion, CancellationToken.None)
            .ConfigureAwait(false);

        if (result.ExitCode == ExitCodes.Ok && result.BodyToStdout is { } body)
        {
            // Defence-in-depth (fail closed): if the endpoint ever echoed the bearer back in the body
            // (a bug or compromise), NEVER emit it to stdout — that stdout is captured by the long-running
            // tray. The token is already in this process's memory, so scanning for it here costs nothing
            // and guarantees it can never reach the tray's captured output.
            if (ResponseContainsToken(body, token))
            {
                WriteReason(ReasonCodes.TokenInResponse);
                return ExitCodes.Schema;
            }

            // Relay the RAW body bytes verbatim — byte-exact, independent of console encoding. This is
            // the ONLY thing ever written to stdout, and it is the usage JSON, never the token.
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(body, 0, body.Length);
            stdout.Flush();
            return ExitCodes.Ok;
        }

        if (result.Reason is not null)
        {
            WriteReason(result.Reason);
        }

        return result.ExitCode;
    }

    /// <summary>The ONLY channel to stderr — always a fixed, token-free reason CODE.</summary>
    private static void WriteReason(string code) => Console.Error.WriteLine(code);

    /// <summary>True if the UTF-8 <paramref name="body"/> contains the access-token byte sequence.</summary>
    private static bool ResponseContainsToken(byte[] body, string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        var needle = System.Text.Encoding.UTF8.GetBytes(token);
        return ContainsSubsequence(body, needle);
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
