namespace ClaudeUsageProbe;

/// <summary>
/// The probe's process exit codes — the primary machine-readable channel the tray reads.
/// Distinct per failure class so the tray can map an outcome to a state without parsing stderr.
/// stdout carries the usage JSON on success ONLY; stderr carries a short, token-free reason CODE on
/// failure ONLY. Exit code 1 is intentionally unused (it is the generic CLR fault code); every path
/// here returns one of these deliberately.
/// </summary>
internal static class ExitCodes
{
    /// <summary>200 OK, body validated as a JSON object, body written verbatim to stdout.</summary>
    public const int Ok = 0;

    /// <summary>Bad/missing arguments (e.g. no <c>--claude-version</c>), or an unsafe test override.</summary>
    public const int Usage = 2;

    /// <summary>Credentials file missing, unreadable, or malformed (no usable access token).</summary>
    public const int Credentials = 3;

    /// <summary>Endpoint rejected the credential: HTTP 401 or 403.</summary>
    public const int AuthRejected = 4;

    /// <summary>Endpoint throttled the request: HTTP 429.</summary>
    public const int Throttled = 5;

    /// <summary>Transport failure: network error, timeout, a 3xx redirect (never followed), or an
    /// unexpected non-2xx status.</summary>
    public const int Transport = 6;

    /// <summary>200 OK but the body was empty or did not parse as a JSON object.</summary>
    public const int Schema = 7;
}
