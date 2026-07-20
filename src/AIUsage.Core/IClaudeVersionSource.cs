namespace AIUsage.Core;

/// <summary>
/// The version-resolution SEAM the <see cref="ClaudeProvider"/> depends on (DESIGN.md §4.2, T23): resolve
/// the installed Claude Code version for the probe's <c>claude-code/&lt;version&gt;</c> User-Agent, or
/// <c>null</c> when none has EVER resolved on this machine (→ the provider shows <c>n/a("no-version")</c>).
/// Abstracted so the provider is unit-testable against a fake that never touches the filesystem or spawns
/// a process; production wires <see cref="ClaudeVersionResolver"/>, which already implements this shape.
/// </summary>
public interface IClaudeVersionSource
{
    /// <summary>
    /// Resolve the version, or <c>null</c> if none has ever resolved on this machine. Never fabricates a
    /// version — a cached previously-real value is truthful, a made-up one is not (DESIGN.md §4.2).
    /// </summary>
    ClaudeVersionResult? Resolve();
}
