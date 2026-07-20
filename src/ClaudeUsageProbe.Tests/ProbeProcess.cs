using System.Diagnostics;
using System.Text;

namespace ClaudeUsageProbe.Tests;

/// <summary>Result of running the probe as a child process.</summary>
internal sealed record ProbeRun(int ExitCode, byte[] StdoutBytes, string Stderr)
{
    /// <summary>stdout decoded as UTF-8 (the E1 body is ASCII, so this is byte-faithful for tests).</summary>
    public string StdoutText => Encoding.UTF8.GetString(StdoutBytes);
}

/// <summary>
/// Launches the REAL <c>ClaudeUsageProbe</c> executable (copied next to this test assembly by the
/// project reference) as a separate process and captures its raw stdout bytes, stderr text, and exit
/// code. Running the actual exe — not an in-process method — is what makes the token-hygiene assertions
/// honest: they inspect the real output streams a caller would see.
/// </summary>
internal static class ProbeProcess
{
    public static ProbeRun Run(
        IReadOnlyList<string> args,
        string? endpointOverride = null)
    {
        var (fileName, prefixArgs) = ResolveProbe();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in prefixArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // The child inherits the parent environment; explicitly clear the override unless the test sets
        // it, so no ambient value leaks in.
        startInfo.Environment[AnthropicUsageEndpoint.OverrideEnvVar] =
            endpointOverride ?? string.Empty;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ClaudeUsageProbe.");

        using var stdoutBuffer = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer);
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }

            throw new TimeoutException("ClaudeUsageProbe did not exit within 30s.");
        }

        stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return new ProbeRun(process.ExitCode, stdoutBuffer.ToArray(), stderr);
    }

    private static (string FileName, string[] PrefixArgs) ResolveProbe()
    {
        var directory = AppContext.BaseDirectory;

        var exe = Path.Combine(directory, "ClaudeUsageProbe.exe");
        if (File.Exists(exe))
        {
            return (exe, Array.Empty<string>());
        }

        var dll = Path.Combine(directory, "ClaudeUsageProbe.dll");
        if (File.Exists(dll))
        {
            return ("dotnet", new[] { dll });
        }

        throw new FileNotFoundException(
            $"ClaudeUsageProbe(.exe/.dll) not found next to the test assembly ({directory}).");
    }
}
