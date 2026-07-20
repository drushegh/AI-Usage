namespace ClaudeUsageProbe;

/// <summary>
/// The single origin + path the bearer token is ever sent to. Hard-coded consts in the shipped default
/// (DESIGN §4.2). A test-only override exists for the local stub server — see the security note.
/// </summary>
internal static class AnthropicUsageEndpoint
{
    /// <summary>The ONLY host the credential is sent to in production. Single source of truth for both
    /// the origin below and the bearer-sink host assertion in <see cref="UsageProbeClient"/>.</summary>
    public const string Host = "api.anthropic.com";

    /// <summary>The ONLY origin the credential is sent to in production.</summary>
    public const string Origin = "https://" + Host;

    /// <summary>The usage path (E1: <c>GET /api/oauth/usage</c>).</summary>
    public const string Path = "/api/oauth/usage";

    /// <summary>
    /// Environment variable that redirects the request to a local stub for tests.
    /// <para>
    /// SECURITY: the override is honoured ONLY when it resolves to a loopback address (127.0.0.0/8,
    /// <c>localhost</c>, or ::1). A non-loopback override is refused outright (exit 2) — the bearer can
    /// therefore never be redirected off-machine by an environment variable. Setting a process's
    /// environment already requires the same user, which is explicitly outside the threat model
    /// (DESIGN §2: same-user code can read <c>.credentials.json</c> directly); the loopback gate narrows
    /// even that residual surface to the local machine and fails loud rather than silently.
    /// </para>
    /// </summary>
    public const string OverrideEnvVar = "CLAUDEUSAGEPROBE_ENDPOINT_OVERRIDE";

    /// <summary>Resolve the target URI from the environment (production path).</summary>
    public static (Uri Endpoint, string? Error) Resolve()
        => Resolve(Environment.GetEnvironmentVariable(OverrideEnvVar));

    /// <summary>
    /// Resolve the target URI from an explicit override value (kept separate so the loopback gate is
    /// unit-testable without mutating process-global environment state). On any error the returned URI
    /// is the real endpoint, but callers MUST check <c>Error</c> first and abort — the request is never
    /// made when an error is set.
    /// </summary>
    internal static (Uri Endpoint, string? Error) Resolve(string? overrideValue)
    {
        var real = new Uri(Origin + Path);

        if (string.IsNullOrEmpty(overrideValue))
        {
            return (real, null);
        }

        if (!Uri.TryCreate(overrideValue, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsLoopback)
        {
            return (real, ReasonCodes.EndpointOverrideNotLoopback);
        }

        return (uri, null);
    }
}
