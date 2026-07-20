namespace ClaudeUsageProbe;

/// <summary>
/// Short, allow-listed reason CODES written to stderr on failure or as an <c>WARN</c> prefix.
/// These are the ONLY strings the probe ever writes to stderr. Every one is fixed and token-free by
/// construction — the token, the Authorization header, and response bodies are NEVER interpolated into
/// any of them (DESIGN §6). <see cref="HttpStatus"/> appends only a numeric HTTP status, which is not
/// sensitive.
/// </summary>
internal static class ReasonCodes
{
    // --- usage / args (exit 2) ---
    public const string Usage = "usage";
    public const string BadVersion = "usage:bad-version";
    public const string EndpointOverrideNotLoopback = "usage:endpoint-override-not-loopback";

    // --- credentials (exit 3) ---
    public const string CredentialsMissing = "credentials-missing";
    public const string CredentialsUnreadable = "credentials-unreadable";
    public const string CredentialsInvalid = "credentials-invalid";

    // --- HTTP outcomes ---
    public const string AuthRejected = "auth-rejected";        // exit 4
    public const string Throttled = "throttled";               // exit 5
    public const string RedirectBlocked = "redirect-blocked";  // exit 6
    public const string Timeout = "timeout";                   // exit 6
    public const string Transport = "transport";               // exit 6
    public const string EndpointNotAllowed = "endpoint-not-allowed"; // exit 6 (bearer-sink host assertion failed)
    public const string HttpStatus = "http-status:";           // exit 6, + numeric status (non-sensitive)
    public const string Schema = "schema";                     // exit 7
    public const string ResponseTooLarge = "response-too-large"; // exit 7 (body exceeded the hard cap)
    public const string TokenInResponse = "token-in-response"; // exit 7 (body echoed the bearer — never relayed)

    // --- token / header hygiene ---
    public const string TokenMalformed = "token-malformed";    // exit 3 (token cannot form a safe header value)

    // --- backstop ---
    public const string Unexpected = "unexpected";             // exit 6 (absolute Main catch-all)

    // --- ACL audit (warn-and-proceed; never changes the exit code) ---
    public const string AclPermissive = "acl-permissive";      // + ":everyone" | ":users" | ":authenticated-users" | ":other-user"
    public const string AclUnreadable = "acl-unreadable";
}
