using System.Text;

namespace AIUsage.Core;

/// <summary>
/// The Codex usage provider (DESIGN.md §4.1): a local, free, zero-network collector that reads
/// OpenAI Codex CLI session files (<c>~/.codex/sessions/**/*.jsonl</c>, honouring <c>CODEX_HOME</c>)
/// and publishes the current rate-limit reading. It resolves and enumerates candidate files, reads a
/// bounded tail of each with tolerant sharing/locking semantics, and delegates ALL parsing and mapping
/// to the pure <see cref="CodexSessionParser"/> — this class is only the I/O shell.
/// </summary>
/// <remarks>
/// <para>
/// <b>Isolation.</b> Every failure mode is contained to an <see cref="SourceStatus.Unavailable"/>
/// snapshot: an absent sessions dir → <c>"no-sessions-dir"</c>; a readable dir with no usable recent
/// event → <c>"no-recent-event"</c>; a file locked or mid-write → that file is skipped and the provider
/// stays up (files are opened <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>). Zero
/// network I/O occurs on any path here.
/// </para>
/// <para>
/// <b>TODO (DESIGN.md §4.1 — not gating T7):</b> a debounced recursive <c>FileSystemWatcher</c> is the
/// intended latency optimisation on top of the reconciliation poll. It is a pure optimisation — the
/// <see cref="ProviderRunner"/>'s <see cref="MinInterval"/> poll is the correctness guarantee — and is
/// deliberately out of scope for this collector unit.
/// </para>
/// </remarks>
public sealed class CodexProvider : IUsageProvider
{
    /// <summary>Stable provider identity and store key (DESIGN.md §3).</summary>
    public const string ProviderId = "codex";

    /// <summary>Reconciliation-poll cadence floor (DESIGN.md §4.1): the loop fetches at most this often.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>Newest-by-mtime files considered per fetch — a candidate PRE-FILTER only; truth is decided by embedded timestamp (e3-findings §4).</summary>
    public const int MaxCandidateFiles = 10;

    /// <summary>Bytes read from the end of each candidate file. 64 KB comfortably covers the worst real event-to-EOF distance (~5.5 KB) measured in E3.</summary>
    public const int TailBytes = 64 * 1024;

    /// <summary>Age cap on candidate files by mtime (e3-findings §4): stale usage should not display, and the scan stays bounded.</summary>
    public static readonly TimeSpan MaxFileAge = TimeSpan.FromDays(14);

    private readonly TimeProvider _timeProvider;
    private readonly string _sessionsDirectory;

    /// <param name="timeProvider">
    /// Clock seam (dotnet-development standard; DESIGN.md §4) used for the snapshot's
    /// <see cref="ProviderSnapshot.FetchedAt"/> and the 14-day candidate age cap — never
    /// <see cref="DateTimeOffset.UtcNow"/> directly.
    /// </param>
    /// <param name="sessionsDirectory">
    /// Explicit sessions directory, or <c>null</c> (default) to resolve it from the environment via
    /// <see cref="ResolveSessionsDirectory"/> (<c>CODEX_HOME/sessions</c> if <c>CODEX_HOME</c> is set,
    /// else <c>~/.codex/sessions</c>). The override exists for tests and any future custom-path config.
    /// </param>
    public CodexProvider(TimeProvider timeProvider, string? sessionsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _sessionsDirectory = sessionsDirectory ?? ResolveSessionsDirectory();
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public TimeSpan MinInterval => DefaultMinInterval;

    /// <summary>The sessions directory this provider reads from (resolved at construction).</summary>
    public string SessionsDirectory => _sessionsDirectory;

    /// <summary>
    /// Resolve the Codex sessions directory from the environment: <c>CODEX_HOME/sessions</c> when
    /// <c>CODEX_HOME</c> is set (non-blank), otherwise <c>&lt;user profile&gt;/.codex/sessions</c>.
    /// Uses <see cref="Environment"/> paths — never a hard-coded absolute path.
    /// </summary>
    public static string ResolveSessionsDirectory()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var baseDirectory = !string.IsNullOrWhiteSpace(codexHome)
            ? codexHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        return Path.Combine(baseDirectory, "sessions");
    }

    /// <inheritdoc />
    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var fetchedAt = _timeProvider.GetUtcNow();

        IReadOnlyList<string> candidates;
        try
        {
            if (!Directory.Exists(_sessionsDirectory))
            {
                return CodexSessionParser.BuildUnavailable("no-sessions-dir", fetchedAt);
            }

            candidates = EnumerateCandidates(fetchedAt);
        }
        catch (DirectoryNotFoundException)
        {
            return CodexSessionParser.BuildUnavailable("no-sessions-dir", fetchedAt);
        }
        catch (IOException)
        {
            return CodexSessionParser.BuildUnavailable("no-sessions-dir", fetchedAt);
        }
        catch (UnauthorizedAccessException)
        {
            return CodexSessionParser.BuildUnavailable("no-sessions-dir", fetchedAt);
        }

        var tails = new List<string>(candidates.Count);
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tail = await ReadTailAsync(path, TailBytes, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(tail))
            {
                tails.Add(tail);
            }
        }

        return CodexSessionParser.BuildSnapshot(tails, fetchedAt);
    }

    /// <summary>
    /// Candidate pre-filter: <c>*.jsonl</c> under the sessions dir, dropping files older than
    /// <see cref="MaxFileAge"/> by mtime, newest-mtime first, capped at <see cref="MaxCandidateFiles"/>.
    /// mtime narrows candidates only — the parser still picks the newest by embedded timestamp across
    /// the whole set, so fall-through past empty/degenerate newest files is automatic (e3-findings §4).
    /// </summary>
    private IReadOnlyList<string> EnumerateCandidates(DateTimeOffset now)
    {
        var cutoff = now - MaxFileAge;
        var files = new List<(string Path, DateTime MTimeUtc)>();

        foreach (var path in Directory.EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories))
        {
            DateTime mtimeUtc;
            try
            {
                mtimeUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                continue; // vanished / locked between enumerate and stat — skip, provider stays up
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (new DateTimeOffset(mtimeUtc) >= cutoff)
            {
                files.Add((path, mtimeUtc));
            }
        }

        files.Sort(static (a, b) => b.MTimeUtc.CompareTo(a.MTimeUtc)); // newest mtime first

        var count = Math.Min(files.Count, MaxCandidateFiles);
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(files[i].Path);
        }

        return result;
    }

    /// <summary>
    /// Read up to <paramref name="maxBytes"/> from the END of a session file. Opens shared for
    /// read/write/delete so a live Codex session (or antivirus/copy) cannot cause a sharing violation;
    /// a genuine <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> returns <c>null</c>
    /// so the file is simply skipped. Decodes as UTF-8 with the replacement fallback — a mid-multibyte
    /// leading fragment corrupts only the first (skipped) line.
    /// </summary>
    private static async Task<string?> ReadTailAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            });

            var length = stream.Length;
            var start = length > maxBytes ? length - maxBytes : 0L;
            var count = (int)(length - start);
            if (count <= 0)
            {
                return string.Empty;
            }

            if (start > 0)
            {
                stream.Seek(start, SeekOrigin.Begin);
            }

            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break; // file truncated concurrently — use what we have
                }

                total += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
