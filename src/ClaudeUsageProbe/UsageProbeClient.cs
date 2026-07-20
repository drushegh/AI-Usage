using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageProbe;

/// <summary>Outcome of the single HTTP call: an exit code, the body to relay on success, and a reason.</summary>
internal sealed record ProbeResult(int ExitCode, byte[]? BodyToStdout, string? Reason);

/// <summary>
/// Makes exactly ONE request: <c>GET https://api.anthropic.com/api/oauth/usage</c> with
/// <c>Authorization: Bearer &lt;token&gt;</c> and <c>User-Agent: claude-code/&lt;ver&gt;</c>.
/// <para>HTTP posture (DESIGN §4.2):</para>
/// <list type="bullet">
///   <item><see cref="HttpClientHandler.AllowAutoRedirect"/> = <c>false</c> — a 3xx is a protocol
///   failure; the bearer must never be carried to a redirect target.</item>
///   <item>No proxy by default; default TLS certificate validation, never relaxed; no cert pinning.</item>
///   <item>15s total timeout covering headers AND the streamed body.</item>
/// </list>
/// <para>Defence-in-depth guards applied BEFORE the token ever reaches the wire:</para>
/// <list type="bullet">
///   <item><b>Host assertion</b> — the bearer is attached only if the target is the exact Anthropic
///   endpoint (https, <c>api.anthropic.com</c>, port 443, no userinfo, expected path) or a loopback stub
///   (already loopback-gated in <see cref="AnthropicUsageEndpoint"/>). Any other URI fails closed.</item>
///   <item><b>Token header validation</b> — a token containing whitespace/control characters or an
///   anomalous length is rejected (fail closed, nothing sent), closing header-splitting injection from a
///   tampered credentials file.</item>
///   <item><b>Response size cap</b> — the body is streamed with a hard byte cap, so a hostile or broken
///   endpoint cannot exhaust memory or starve the other provider.</item>
/// </list>
/// <para>
/// SECURITY: the whole call is wrapped so a thrown exception can never surface the Authorization header
/// or token — only fixed reason codes are ever returned. Exception messages / <c>ToString()</c> are
/// never read into any output.
/// </para>
/// </summary>
internal static class UsageProbeClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Hard cap on the response body we will buffer (256 KiB). The real usage JSON is a couple of
    /// KiB; anything larger is rejected rather than buffered.</summary>
    private const int MaxResponseBytes = 256 * 1024;

    /// <summary>Generous upper bound on a bearer token length; a real OAuth access token is a few hundred
    /// characters. Bounds a pathological credentials file.</summary>
    private const int MaxTokenLength = 8 * 1024;

    public static async Task<ProbeResult> ExecuteAsync(
        Uri endpoint,
        string bearerToken,
        string claudeVersion,
        CancellationToken cancellationToken)
    {
        // Host assertion (defence in depth): attach the bearer ONLY to the real Anthropic usage endpoint
        // or a loopback stub. A different URI — from a future caller or resolver defect — fails closed
        // here, before the token is ever put on the wire.
        if (!IsTokenSinkAllowed(endpoint))
        {
            return new ProbeResult(ExitCodes.Transport, null, ReasonCodes.EndpointNotAllowed);
        }

        // Token header validation: reject a token that cannot form a well-formed header value BEFORE any
        // request is built or sent, so a tampered credentials file cannot inject header-splitting content
        // and nothing is transmitted.
        if (!TryBuildBearerHeader(bearerToken, out var authorization) || authorization is null)
        {
            return new ProbeResult(ExitCodes.Credentials, null, ReasonCodes.TokenMalformed);
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, // a 3xx must NEVER carry the bearer elsewhere
            UseProxy = false,          // no proxy by default (DESIGN §4.2 / §6)
            // Certificate validation is left at the platform default — never a custom callback, never
            // relaxed, no pinning. SslProtocols is left unset (SslProtocols.None => OS default), which is
            // TLS 1.2/1.3 on supported Windows; hard-coding the list is discouraged by the platform.
        };

        using var client = new HttpClient(handler) { Timeout = Timeout };

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        // The token was validated above and is installed through the typed Authorization property (which
        // also validates), never a raw TryAddWithoutValidation of "Bearer " + token.
        request.Headers.Authorization = authorization;

        // The version is already control-char-validated; TryAddWithoutValidation keeps it verbatim. The
        // return is still checked as a belt-and-braces fail-closed.
        if (!request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/" + claudeVersion))
        {
            return new ProbeResult(ExitCodes.Usage, null, ReasonCodes.BadVersion);
        }

        // Bound the WHOLE operation (headers + streamed body) at 15s. With ResponseHeadersRead,
        // HttpClient.Timeout does not cover the body read, so a linked CTS supplies the deadline the
        // production call site (which passes CancellationToken.None) otherwise lacks.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        var ct = timeoutCts.Token;

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout / the linked deadline surface as (Task)OperationCanceledException.
            return new ProbeResult(ExitCodes.Transport, null, ReasonCodes.Timeout);
        }
        catch (Exception)
        {
            // HttpRequestException and anything else: never read the message (it may name the request).
            return new ProbeResult(ExitCodes.Transport, null, ReasonCodes.Transport);
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            if (status is >= 300 and <= 399)
            {
                return new ProbeResult(ExitCodes.Transport, null, ReasonCodes.RedirectBlocked);
            }

            if (status is 401 or 403)
            {
                return new ProbeResult(ExitCodes.AuthRejected, null, ReasonCodes.AuthRejected);
            }

            if (status == 429)
            {
                return new ProbeResult(ExitCodes.Throttled, null, ReasonCodes.Throttled);
            }

            if (status != 200)
            {
                return new ProbeResult(ExitCodes.Transport, null, ReasonCodes.HttpStatus + status);
            }

            // Response size cap: short-circuit an oversized declared length, then enforce the same cap
            // while streaming (without trusting the Content-Length header).
            if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
            {
                return new ProbeResult(ExitCodes.Schema, null, ReasonCodes.ResponseTooLarge);
            }

            byte[]? body;
            try
            {
                body = await ReadCappedAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ProbeResult(ExitCodes.Transport, null, ReasonCodes.Timeout);
            }
            catch (Exception)
            {
                return new ProbeResult(ExitCodes.Transport, null, ReasonCodes.Transport);
            }

            if (body is null)
            {
                // Body exceeded the hard cap — never buffer or relay it.
                return new ProbeResult(ExitCodes.Schema, null, ReasonCodes.ResponseTooLarge);
            }

            if (body.Length == 0 || !IsJsonObject(body))
            {
                // Empty or non-JSON (e.g. an HTML error page) — do NOT relay it to stdout.
                return new ProbeResult(ExitCodes.Schema, null, ReasonCodes.Schema);
            }

            return new ProbeResult(ExitCodes.Ok, body, null);
        }
    }

    /// <summary>
    /// The bearer may ONLY be attached to the real Anthropic usage endpoint (https, exact host, port 443,
    /// no userinfo, expected path) or a loopback stub — the latter is already gated to loopback in
    /// <see cref="AnthropicUsageEndpoint.Resolve(string?)"/>. Everything else fails closed.
    /// </summary>
    internal static bool IsTokenSinkAllowed(Uri uri)
    {
        // Loopback targets are the test stub, already loopback-gated at endpoint resolution.
        if (uri.IsLoopback)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.IdnHost, AnthropicUsageEndpoint.Host, StringComparison.Ordinal)
            && uri.Port == 443
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.AbsolutePath, AnthropicUsageEndpoint.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates the token can form a safe <c>Authorization</c> header value and builds it. Rejects an
    /// empty/overlong token or one containing any whitespace or control character (an OAuth bearer is a
    /// compact token68-style string), then constructs the validated header. Fails closed on any anomaly.
    /// </summary>
    internal static bool TryBuildBearerHeader(string token, out AuthenticationHeaderValue? header)
    {
        header = null;

        if (token.Length is 0 or > MaxTokenLength)
        {
            return false;
        }

        foreach (var ch in token)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        try
        {
            header = new AuthenticationHeaderValue("Bearer", token);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Streams the response body with a hard byte cap. Returns the buffered bytes, or <c>null</c> if the
    /// body exceeds <paramref name="cap"/> (in which case reading stops immediately — the rest is never
    /// buffered). Never buffers more than <paramref name="cap"/> bytes.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int cap, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        int read;
        while ((read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                return null; // exceeded the cap — abort without buffering the rest
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Minimal validation: the body parses as a JSON object. The tray does the full schema
    /// parse; the probe only relays a validated-as-JSON envelope.</summary>
    private static bool IsJsonObject(byte[] utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(utf8));
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
