namespace AIUsage.Core.Tests;

/// <summary>
/// An in-memory <see cref="IVersionFileSystem"/> for the resolution-ORDER tests: no real machine paths,
/// no real <c>claude.exe</c>. Writes persist into the same text store, so a cache write-through is
/// observable by a later <see cref="IVersionFileSystem.ReadText"/> on the same instance (the round-trip).
/// </summary>
internal sealed class FakeVersionFileSystem : IVersionFileSystem
{
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _exeVersions = new(StringComparer.Ordinal);

    /// <summary>Paths written to, in order — lets a test assert the cache write-through happened.</summary>
    public List<string> Writes { get; } = new();

    public string? ReadText(string path) => _text.TryGetValue(path, out var value) ? value : null;

    public string? ReadExeProductVersion(string path) => _exeVersions.TryGetValue(path, out var value) ? value : null;

    public void WriteText(string path, string content)
    {
        _text[path] = content;
        Writes.Add(path);
    }

    public FakeVersionFileSystem SetText(string path, string content)
    {
        _text[path] = content;
        return this;
    }

    public FakeVersionFileSystem SetExeVersion(string path, string productVersion)
    {
        _exeVersions[path] = productVersion;
        return this;
    }
}

/// <summary>
/// A fake <see cref="IClaudeVersionCommandRunner"/> that returns canned stdout without spawning anything.
/// Records the call count + last command so a test can assert the CLI step was (or was not) reached.
/// </summary>
internal sealed class FakeClaudeVersionCommandRunner : IClaudeVersionCommandRunner
{
    private readonly string? _output;

    public FakeClaudeVersionCommandRunner(string? output = null) => _output = output;

    public int Calls { get; private set; }

    public string? LastCommand { get; private set; }

    public string? Run(string command, TimeSpan timeout)
    {
        Calls++;
        LastCommand = command;
        return _output;
    }
}
