using System.Diagnostics;
using System.Text;

namespace AIUsage.Core;

/// <summary>
/// The result of one probe run — the exit code (the primary machine-readable channel) plus the usage
/// JSON the probe wrote to stdout on success. <see cref="StdoutJson"/> is non-null ONLY for
/// <see cref="ClaudeProbeExitCodes.Ok"/>; every failure code carries a null body (the failure detail is
/// the exit code, never a logged response body). The access token appears in NEITHER field — the probe
/// writes only the usage JSON to stdout, never the credential (DESIGN.md §2b/§6).
/// </summary>
/// <param name="ExitCode">The probe's process exit code (see <see cref="ClaudeProbeExitCodes"/>).</param>
/// <param name="StdoutJson">The raw usage JSON body on success; <c>null</c> on any failure.</param>
public sealed record ClaudeProbeResult(int ExitCode, string? StdoutJson);

/// <summary>
/// The result of a size-capped stdout read (<see cref="ProcessClaudeProbeRunner.ReadCappedAsync"/>):
/// the captured <paramref name="Text"/>, or <paramref name="Overflow"/> = <c>true</c> (with empty text)
/// when the output exceeded the hard cap and was rejected wholesale (P2-6).
/// </summary>
/// <param name="Text">The captured characters (empty when <paramref name="Overflow"/> is true).</param>
/// <param name="Overflow">True when the output exceeded the cap and nothing was retained.</param>
public readonly record struct CappedStdout(string Text, bool Overflow);

/// <summary>
/// The tray-side mirror of <c>ClaudeUsageProbe.ExitCodes</c> — the exit-code CONTRACT across the process
/// boundary (DESIGN.md §2b/§4.2). It MUST stay in sync with <c>src/ClaudeUsageProbe/ExitCodes.cs</c>; the
/// two projects share no code (the domain core is BCL-only and never references the probe exe), so this
/// small constant table is the seam. Exit code 1 is intentionally unused (the generic CLR fault code).
/// </summary>
public static class ClaudeProbeExitCodes
{
    /// <summary>200 OK, body validated as a JSON object and relayed to stdout.</summary>
    public const int Ok = 0;

    /// <summary>Bad/missing arguments (should never occur — the tray controls argv).</summary>
    public const int Usage = 2;

    /// <summary>Credentials file missing, unreadable, or malformed (no usable access token).</summary>
    public const int Credentials = 3;

    /// <summary>Endpoint rejected the credential: HTTP 401 or 403.</summary>
    public const int AuthRejected = 4;

    /// <summary>Endpoint throttled the request: HTTP 429.</summary>
    public const int Throttled = 5;

    /// <summary>Transport failure: network error, timeout, a 3xx redirect (never followed), or an unexpected non-2xx status.</summary>
    public const int Transport = 6;

    /// <summary>200 OK but the body was empty or did not parse as a JSON object.</summary>
    public const int Schema = 7;
}

/// <summary>
/// The probe-execution SEAM (DESIGN.md §2b, §4.2): run one <c>ClaudeUsageProbe.exe</c> invocation with the
/// resolved Claude Code version and return its outcome. Abstracted so the <see cref="ClaudeProvider"/> is
/// unit-testable with a FAKE that never spawns the real exe and never touches the network; production wires
/// <see cref="ProcessClaudeProbeRunner"/>.
/// </summary>
public interface IClaudeProbeRunner
{
    /// <summary>
    /// Run the probe with <paramref name="claudeVersion"/> in the User-Agent, returning its exit code and
    /// (on success only) the usage JSON. Implementations MUST never surface the access token in any form.
    /// </summary>
    Task<ClaudeProbeResult> RunAsync(string claudeVersion, CancellationToken cancellationToken);
}

/// <summary>
/// The production <see cref="IClaudeProbeRunner"/>: spawns <c>ClaudeUsageProbe.exe</c> by ABSOLUTE path —
/// resolved next to the tray exe via <see cref="AppContext.BaseDirectory"/> — with no shell, passing only
/// <c>--claude-version &lt;ver&gt;</c> (the version is not a secret). It captures stdout (the usage JSON),
/// drains stderr without surfacing it (stderr carries token-free reason CODES; the exit code is the failure
/// channel), and never sees or logs the access token — that lives and dies inside the child process
/// (DESIGN.md §6). Never used in the unit suite (tests inject a fake), only integration/manual runs.
/// </summary>
public sealed class ProcessClaudeProbeRunner : IClaudeProbeRunner
{
    /// <summary>The probe executable's file name — expected to sit beside the tray exe in the install dir (DESIGN.md §2/§6, T42).</summary>
    public const string ProbeExecutableName = "ClaudeUsageProbe.exe";

    /// <summary>
    /// Hard cap on the characters captured from the probe's stdout (P2-6). The real usage body is a few KB;
    /// a pathological or hostile probe output beyond this is treated as a schema failure rather than being
    /// buffered without bound and handed to the JSON parser (which would blow up memory / degrade the tray).
    /// </summary>
    public const int MaxStdoutChars = 256 * 1024;

    private const string ClaudeVersionArgument = "--claude-version";

    private readonly string _probePath;

    /// <param name="probePath">
    /// Absolute path to the probe exe. Defaults to <see cref="ProbeExecutableName"/> resolved next to the
    /// running tray exe (<see cref="AppContext.BaseDirectory"/>) — never a shell lookup or a relative path.
    /// </param>
    public ProcessClaudeProbeRunner(string? probePath = null)
        => _probePath = probePath ?? Path.Combine(AppContext.BaseDirectory, ProbeExecutableName);

    /// <summary>The absolute probe path this runner launches.</summary>
    public string ProbePath => _probePath;

    /// <summary>
    /// Build the probe's <see cref="ProcessStartInfo"/>: absolute path, no shell, individually-escaped args,
    /// and — critically — <b>UTF-8 output encoding</b> on both pipes (P2-6) so the probe's byte-exact UTF-8
    /// usage JSON is decoded as UTF-8, not the parent WinExe's OS ANSI/OEM codepage (which would garble a
    /// non-ASCII model name or currency glyph into a false "source-changed" drift). Exposed for the unit
    /// suite to assert the encoding without spawning a real process.
    /// </summary>
    public ProcessStartInfo CreateStartInfo(string claudeVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeVersion);

        var startInfo = new ProcessStartInfo
        {
            FileName = _probePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, // decode the probe's raw UTF-8 body as UTF-8 (P2-6)
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, // no shell — absolute path, no argument re-parsing (DESIGN.md §6)
            CreateNoWindow = true,
        };

        // ArgumentList escapes each argument individually — no command-line string to inject into.
        startInfo.ArgumentList.Add(ClaudeVersionArgument);
        startInfo.ArgumentList.Add(claudeVersion);
        return startInfo;
    }

    /// <inheritdoc />
    public async Task<ClaudeProbeResult> RunAsync(string claudeVersion, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(claudeVersion);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ClaudeProbeResult(ClaudeProbeExitCodes.Transport, null);
            }

            // Start draining both pipes BEFORE waiting so a child that fills a pipe can't deadlock. stdout is
            // read WITH A HARD CAP (P2-6) so a pathological body can't blow up memory; stderr is drained and
            // DISCARDED — it only ever holds token-free reason codes, and the exit code is the failure channel.
            var stdoutTask = ReadCappedAsync(process.StandardOutput, MaxStdoutChars, cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false); // drained, never surfaced

            var exitCode = process.ExitCode;
            if (exitCode != ClaudeProbeExitCodes.Ok)
            {
                // Only a successful run carries a usable body; every other code ignores stdout entirely.
                return new ClaudeProbeResult(exitCode, null);
            }

            // A 200 whose body exceeded the cap is untrustable → treat it as a schema failure rather than
            // relaying a truncated/oversized body to the parser.
            return stdout.Overflow
                ? new ClaudeProbeResult(ClaudeProbeExitCodes.Schema, null)
                : new ClaudeProbeResult(ClaudeProbeExitCodes.Ok, stdout.Text);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw; // the runner (ProviderRunner) treats cancellation as shutdown
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            // A launch/pipe failure is a transport failure, never fatal. The token never entered this
            // process, so there is nothing to scrub.
            TryKill(process);
            return new ClaudeProbeResult(ClaudeProbeExitCodes.Transport, null);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Read <paramref name="reader"/> to end but cap the captured characters at <paramref name="maxChars"/>
    /// (P2-6). On overflow it keeps draining (so the child can exit without blocking on a full pipe) but
    /// discards the excess and reports <see cref="CappedStdout.Overflow"/>. Exposed for direct unit testing
    /// of the cap without spawning a process.
    /// </summary>
    public static async Task<CappedStdout> ReadCappedAsync(TextReader reader, int maxChars, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        var buffer = new char[8192];
        var builder = new StringBuilder();
        var overflow = false;

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (overflow)
            {
                continue; // already over the cap — keep draining to let the child exit, but keep nothing
            }

            builder.Append(buffer, 0, read);
            if (builder.Length > maxChars)
            {
                overflow = true;
                builder.Clear(); // release the buffered excess; the body is rejected wholesale
            }
        }

        return overflow ? new CappedStdout(string.Empty, true) : new CappedStdout(builder.ToString(), false);
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited or un-killable — nothing more to do.
        }
    }
}
