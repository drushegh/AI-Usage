using System.Text;
using Xunit;

namespace AIUsage.Core.Tests;

/// <summary>
/// Unit tests for the probe-runner SEAM's testable pieces (P2-6): the UTF-8 output encoding on the process
/// start-info, and the size-capped stdout reader. Neither spawns a real process — <c>CreateStartInfo</c> is
/// inspected directly and <c>ReadCappedAsync</c> is driven with an in-memory reader — so they stay fast and
/// deterministic. The full spawn path is covered by integration/manual runs.
/// </summary>
public sealed class ProcessClaudeProbeRunnerTests
{
    private const string ProbePath = @"C:\install\ClaudeUsageProbe.exe";

    [Fact]
    public void CreateStartInfo_UsesUtf8Encoding_OnBothPipes_AndSafeLaunchFlags()
    {
        var runner = new ProcessClaudeProbeRunner(ProbePath);

        var startInfo = runner.CreateStartInfo("2.1.191");

        // UTF-8 on BOTH pipes — the crux of P2-6: the probe writes byte-exact UTF-8, so the parent must
        // decode it as UTF-8, not the OS ANSI/OEM codepage.
        Assert.Equal(Encoding.UTF8, startInfo.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, startInfo.StandardErrorEncoding);

        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute); // no shell — no argument re-parsing
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProbePath, startInfo.FileName);
        Assert.Equal(new[] { "--claude-version", "2.1.191" }, startInfo.ArgumentList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateStartInfo_RejectsBlankVersion(string? version)
    {
        var runner = new ProcessClaudeProbeRunner(ProbePath);

        Assert.ThrowsAny<ArgumentException>(() => runner.CreateStartInfo(version!));
    }

    [Fact]
    public async Task ReadCappedAsync_UnderTheCap_ReturnsTheWholeBody()
    {
        const string body = "{\"five_hour\":{\"utilization\":42}}";
        using var reader = new StringReader(body);

        var result = await ProcessClaudeProbeRunner.ReadCappedAsync(
            reader, ProcessClaudeProbeRunner.MaxStdoutChars, CancellationToken.None);

        Assert.False(result.Overflow);
        Assert.Equal(body, result.Text);
    }

    [Fact]
    public async Task ReadCappedAsync_ExactlyAtTheCap_IsNotOverflow()
    {
        var exact = new string('y', 256);
        using var reader = new StringReader(exact);

        var result = await ProcessClaudeProbeRunner.ReadCappedAsync(reader, maxChars: 256, CancellationToken.None);

        Assert.False(result.Overflow);
        Assert.Equal(exact, result.Text);
    }

    [Fact]
    public async Task ReadCappedAsync_OverTheCap_ReportsOverflow_AndKeepsNothing()
    {
        // A pathological body far past the cap: overflow is reported and no text is retained, so nothing
        // oversized ever reaches the JSON parser.
        var big = new string('x', 8192 * 4);
        using var reader = new StringReader(big);

        var result = await ProcessClaudeProbeRunner.ReadCappedAsync(reader, maxChars: 256, CancellationToken.None);

        Assert.True(result.Overflow);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task ReadCappedAsync_EmptyBody_IsNotOverflow_AndEmpty()
    {
        using var reader = new StringReader(string.Empty);

        var result = await ProcessClaudeProbeRunner.ReadCappedAsync(reader, maxChars: 256, CancellationToken.None);

        Assert.False(result.Overflow);
        Assert.Equal(string.Empty, result.Text);
    }
}
