using System.Diagnostics;

namespace AIUsage.Core;

/// <summary>
/// The process-spawn seam for the fragile last-live fallback: a TIME-LIMITED <c>claude --version</c>
/// (E2 §2/§3). Abstracted so the resolver never spawns a real process in unit tests — a fake runner
/// returns canned stdout deterministically. Non-throwing: a launch failure or a timeout is a MISS
/// (<c>null</c>), never an exception.
/// </summary>
public interface IClaudeVersionCommandRunner
{
    /// <summary>
    /// Run <c>&lt;command&gt; --version</c> with a hard <paramref name="timeout"/> (killed on expiry) and
    /// return its stdout, or <c>null</c> on launch failure / timeout / empty output.
    /// </summary>
    /// <param name="command">The executable to spawn — the absolute <c>claude.exe</c> path, or bare <c>"claude"</c> if PATH is trusted.</param>
    /// <param name="timeout">The hard wall-clock cap (E2 §3 budgets ~3–5 s; a cold logon start is slower than the ~0.5 s warm run).</param>
    string? Run(string command, TimeSpan timeout);
}

/// <summary>
/// The production <see cref="IClaudeVersionCommandRunner"/>: a real, killed-on-timeout
/// <see cref="Process"/>. Never used in unit tests (they inject a fake) — only in integration/manual runs.
/// </summary>
public sealed class ProcessClaudeVersionCommandRunner : IClaudeVersionCommandRunner
{
    /// <inheritdoc />
    public string? Run(string command, TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            // Drain BOTH pipes asynchronously so a child that writes before exit can't deadlock. stderr is
            // redirected too: leaving it undrained means a child writing more than the pipe buffer (~4 KB)
            // blocks on that write before exit, the WaitForExit below times out, and a resolvable version
            // becomes a spurious miss (P2-16). We read stderr concurrently and discard it.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)))
            {
                TryKill(process);
                return null;
            }

            // Ensure async stream readers have fully flushed before reading the result.
            process.WaitForExit();
            _ = stderr.GetAwaiter().GetResult(); // drained, never surfaced
            var output = stdout.GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited or un-killable — nothing more to do; we return null regardless.
        }
    }
}
