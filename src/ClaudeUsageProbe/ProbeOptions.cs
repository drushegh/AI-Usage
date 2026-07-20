namespace ClaudeUsageProbe;

/// <summary>
/// Parsed and validated command-line options.
/// <para>
/// <c>--claude-version &lt;ver&gt;</c> is REQUIRED and is used verbatim in the <c>claude-code/&lt;ver&gt;</c>
/// User-Agent — the tray resolves and passes it; the probe never guesses a version (DESIGN §4.2, E2).
/// The value is validated to contain no control characters, which also closes CR/LF header-injection
/// into the User-Agent line.
/// </para>
/// <para>
/// <c>--credentials &lt;path&gt;</c> is optional and defaults to
/// <c>%USERPROFILE%\.claude\.credentials.json</c>.
/// </para>
/// </summary>
internal sealed class ProbeOptions
{
    private const int MaxVersionLength = 128;

    public required string ClaudeVersion { get; init; }
    public required string CredentialsPath { get; init; }

    public static bool TryParse(string[] args, out ProbeOptions? options, out string? error)
    {
        options = null;
        error = null;

        string? version = null;
        string? credentials = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--claude-version":
                    if (++i >= args.Length)
                    {
                        error = ReasonCodes.Usage;
                        return false;
                    }
                    version = args[i];
                    break;

                case "--credentials":
                    if (++i >= args.Length)
                    {
                        error = ReasonCodes.Usage;
                        return false;
                    }
                    credentials = args[i];
                    break;

                default:
                    error = ReasonCodes.Usage;
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            error = ReasonCodes.Usage;
            return false;
        }

        if (!IsValidVersion(version))
        {
            error = ReasonCodes.BadVersion;
            return false;
        }

        credentials = string.IsNullOrEmpty(credentials) ? DefaultCredentialsPath() : credentials;

        options = new ProbeOptions { ClaudeVersion = version, CredentialsPath = credentials };
        return true;
    }

    /// <summary>
    /// A version string is only accepted if it is a bounded, control-character-free token. Rejecting
    /// control characters (in particular CR/LF and NUL) prevents header injection when the value is
    /// concatenated into the <c>User-Agent</c> header.
    /// </summary>
    internal static bool IsValidVersion(string version)
    {
        if (version.Length is 0 or > MaxVersionLength)
        {
            return false;
        }

        foreach (var ch in version)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    internal static string DefaultCredentialsPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".claude", ".credentials.json");
    }
}
