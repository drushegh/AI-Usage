using System.Diagnostics;

namespace AIUsage.Core;

/// <summary>
/// The filesystem seam the <see cref="ClaudeVersionResolver"/> reads through (spawn-free sources +
/// the cache). Abstracted so the resolver's RESOLUTION-ORDER logic is unit-testable against temp files
/// and fakes — no real machine paths, no real <c>claude.exe</c> — while production wires
/// <see cref="SystemVersionFileSystem"/>. Every method is non-throwing: an unreadable/absent source is a
/// MISS (<c>null</c>), never an exception, so a single bad source never aborts the resolution chain.
/// </summary>
public interface IVersionFileSystem
{
    /// <summary>Read a file's text, or <c>null</c> if it is absent or unreadable. Never throws.</summary>
    string? ReadText(string path);

    /// <summary>
    /// The <c>ProductVersion</c> of a native executable's Windows version resource (E2 §2b), or
    /// <c>null</c> if the file is absent or carries no version metadata. Never throws.
    /// </summary>
    string? ReadExeProductVersion(string path);

    /// <summary>
    /// Write the last-known-good cache text, creating the parent directory if needed. A failure is
    /// swallowed — persisting the cache is best-effort and must never fail a resolution that already
    /// produced a live version (DESIGN.md §4.2).
    /// </summary>
    void WriteText(string path, string content);
}

/// <summary>
/// The production <see cref="IVersionFileSystem"/>: real <see cref="File"/> reads/writes and
/// <see cref="FileVersionInfo"/>. Thin and I/O-bound by design — the testable logic lives in
/// <see cref="ClaudeVersionResolver"/>, so this adapter is exercised only in integration/manual runs, not
/// the unit suite.
/// </summary>
public sealed class SystemVersionFileSystem : IVersionFileSystem
{
    /// <inheritdoc />
    public string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? ReadExeProductVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // ProductVersion is the E2 §2b source ("2.1.191.0"); may be null/blank on a resource-less binary.
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void WriteText(string path, string content)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best-effort cache write — a live version was already resolved; failing to persist it is not fatal.
        }
    }
}
