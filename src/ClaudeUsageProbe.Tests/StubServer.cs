using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ClaudeUsageProbe.Tests;

/// <summary>
/// A local HTTP stub bound to a random loopback port — the ONLY server the tests point the probe at
/// (never the real <c>api.anthropic.com</c>). It records the first request's method, path, and the
/// <c>Authorization</c> / <c>User-Agent</c> headers, counts requests (to prove a 3xx is NOT followed),
/// and replies with a configured status/body/headers.
/// </summary>
internal sealed class StubServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _status;
    private readonly byte[] _body;
    private readonly string? _contentType;
    private readonly string? _location;
    private readonly bool _chunked;

    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);
    public string? LastAuthorization { get; private set; }
    public string? LastUserAgent { get; private set; }
    public string? LastPath { get; private set; }
    public string? LastMethod { get; private set; }

    /// <summary>The full usage URL to hand the probe via the endpoint-override env var.</summary>
    public string UsageUrl { get; }

    private StubServer(int status, byte[] body, string? contentType, string? location, bool chunked = false)
    {
        _status = status;
        _body = body;
        _contentType = contentType;
        _location = location;
        _chunked = chunked;

        var port = FreeLoopbackPort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        UsageUrl = $"http://127.0.0.1:{port}{AnthropicUsageEndpoint.Path}";

        _ = Task.Run(AcceptLoopAsync);
    }

    public static StubServer Ok(byte[] jsonBody) => new(200, jsonBody, "application/json", null);

    /// <summary>A 200 that streams the body with chunked transfer encoding (NO Content-Length), so the
    /// probe's streaming size cap — not the declared length — is what must reject an oversized body.</summary>
    public static StubServer OkChunked(byte[] jsonBody) => new(200, jsonBody, "application/json", null, chunked: true);

    public static StubServer OkRaw(byte[] body, string contentType) => new(200, body, contentType, null);

    public static StubServer Status(int status) => new(status, Array.Empty<byte>(), null, null);

    public static StubServer Redirect(int status, string location) => new(status, Array.Empty<byte>(), null, location);

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break; // listener stopped
            }

            Interlocked.Increment(ref _requestCount);
            LastMethod = context.Request.HttpMethod;
            LastPath = context.Request.Url?.AbsolutePath;
            LastAuthorization = context.Request.Headers["Authorization"];
            LastUserAgent = context.Request.Headers["User-Agent"];

            try
            {
                context.Response.StatusCode = _status;
                if (_location is not null)
                {
                    context.Response.Headers["Location"] = _location;
                }

                if (_contentType is not null)
                {
                    context.Response.ContentType = _contentType;
                }

                if (_body.Length > 0)
                {
                    if (_chunked)
                    {
                        // No Content-Length: force a genuinely streamed body written in blocks, so the
                        // probe must enforce its cap while reading rather than from a declared length.
                        context.Response.SendChunked = true;
                        var offset = 0;
                        while (offset < _body.Length)
                        {
                            var count = Math.Min(16 * 1024, _body.Length - offset);
                            await context.Response.OutputStream.WriteAsync(_body.AsMemory(offset, count)).ConfigureAwait(false);
                            await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                            offset += count;
                        }
                    }
                    else
                    {
                        context.Response.ContentLength64 = _body.Length;
                        await context.Response.OutputStream.WriteAsync(_body).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // Ignore write races on teardown.
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // Ignore.
                }
            }
        }
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Ignore.
        }
    }

    public static byte[] Html(string text) => Encoding.UTF8.GetBytes(text);
}
