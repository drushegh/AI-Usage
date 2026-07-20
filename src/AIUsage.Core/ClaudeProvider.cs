using System.Text.Json;

namespace AIUsage.Core;

/// <summary>
/// The Claude usage provider (DESIGN.md §4.2): a remote, undocumented, ToS-grey, EXPLICIT-OPT-IN collector.
/// It never touches the network or the credential itself — a short-lived <c>ClaudeUsageProbe.exe</c> does
/// that (DESIGN.md §2b/§6) — this class owns the compliant CADENCE and the honest failure→n/a mapping.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authoritative gate.</b> The 180s hard gate is the provider's own INTERNAL self-gate
/// (<see cref="NextEligibleAt"/>), not just the runner's <see cref="MinInterval"/>. Because manual refresh
/// calls <see cref="FetchAsync"/> DIRECTLY (bypassing the runner), the internal gate is what coalesces the
/// runner tick + manual refresh + restart + resume + credential-file change through the one gate — none
/// bypasses it. Before the next eligible instant, <see cref="FetchAsync"/> returns the last published
/// snapshot unchanged and spawns nothing; at most one probe is ever in flight (DESIGN.md §4.2; T27).
/// </para>
/// <para>
/// <b>Cadence robustness.</b> The gate is enforced three ways: (1) a per-process MONOTONIC floor
/// (<see cref="TimeProvider.GetTimestamp"/>) is authoritative once the process has reserved at least once —
/// immune to any wall-clock adjustment (a forward jump cannot open it early; a backward jump cannot fire it
/// early); (2) the cadence is RESERVED atomically before the probe launches, so a cancelled/indeterminate
/// attempt still holds the gate (the probe may already have sent); (3) the reservation is PERSISTED to
/// <c>%LOCALAPPDATA%\AIUsage\claude-gate.json</c> and reloaded on construction (clamped to at most
/// <c>now + 60min</c>) so a restart — or a crash loop — cannot bypass the cadence (P1-2; DESIGN.md §2/§4.2).
/// The persisted wall value gates ONLY the first post-restart attempt; from the first in-process reservation
/// on, the monotonic floor governs.
/// </para>
/// <para>
/// <b>Backoff.</b> Transient failures walk the explicit ladder 3 / 6 / 12 / 24 / 48 minutes, capped at 60;
/// a success resets to the 180s base (T27). A <c>Retry-After</c> is honoured only if the probe surfaces one
/// — it does not today (TODO: thread a Retry-After through <see cref="ClaudeProbeResult"/>; the ladder is
/// the interim, DESIGN.md §4.2).
/// </para>
/// <para>
/// <b>Version self-heal (P1-3).</b> The Claude Code version (the probe's User-Agent) is resolved once and
/// cached, re-resolved OPPORTUNISTICALLY: daily, and immediately on entering the
/// <c>endpoint-or-UA-changed</c> diagnostic state — so a fixed version sitting on disk heals the stuck
/// state without a restart (DESIGN.md §4.2 "on startup, on version-file change, daily").
/// </para>
/// <para>
/// <b>Opt-in / kill switch (T32).</b> <see cref="Enabled"/> defaults OFF and is toggleable at runtime.
/// Disabled → <c>Unavailable("disabled")</c> WITHOUT resolving a version or spawning a probe, and it
/// CANCELS any in-flight attempt so a late result can never publish <c>Ok</c> over the disabled snapshot.
/// There is no silent fallback to cookies, browser automation, or local-file estimates — the honest states
/// are the only states.
/// </para>
/// </remarks>
public sealed class ClaudeProvider : IUsageProvider
{
    /// <summary>Stable provider identity and store key (DESIGN.md §3).</summary>
    public const string ProviderId = "claude";

    /// <summary>The 180s hard gate — the runner tick floor AND the base cadence a success resets to (DESIGN.md §4.2, T27).</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(180);

    /// <summary>How often the cached Claude Code version is re-resolved opportunistically (DESIGN.md §4.2 "daily").</summary>
    public static readonly TimeSpan VersionRefreshInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// The explicit transient-failure backoff ladder (DESIGN.md §4.2, T27): 3 / 6 / 12 / 24 / 48 minutes,
    /// capped at 60. Walked on consecutive failures; reset to <see cref="DefaultMinInterval"/> on success.
    /// A table (not a formula) so it is trivially testable and auditable.
    /// </summary>
    private static readonly TimeSpan[] BackoffLadder =
    {
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(6),
        TimeSpan.FromMinutes(12),
        TimeSpan.FromMinutes(24),
        TimeSpan.FromMinutes(48),
        TimeSpan.FromMinutes(60),
    };

    /// <summary>
    /// Consecutive 429s at compliant cadence after which a 429 stops meaning "throttled" and becomes the
    /// distinct diagnostic <c>endpoint-or-UA-changed</c> state (DESIGN.md §4.2, T28) — an endless
    /// "throttled" loop is a lie when the real cause is UA/endpoint rejection.
    /// </summary>
    public const int MaxConsecutiveThrottleBeforeDiagnostic = 5;

    private readonly IClaudeProbeRunner _probeRunner;
    private readonly IClaudeVersionSource _versionSource;
    private readonly TimeProvider _timeProvider;
    private readonly IClaudeGateStore _gateStore;

    private readonly object _gate = new();
    private ProviderSnapshot? _last;         // last published snapshot — returned on a gated/in-flight call
    private long _nextEligibleUtcTicks;      // the persisted/cross-restart wall reservation; 0 lets the first fetch run
    private long _monotonicEligibleTimestamp; // the in-process monotonic floor (TimeProvider.GetTimestamp units)
    private bool _hasMonotonicFloor;          // false until the first in-process reservation → wall value governs
    private int _backoffIndex = -1;          // -1 = base cadence; 0.. indexes BackoffLadder
    private int _consecutiveThrottle;        // consecutive 429 count (reset by any non-429 outcome)
    private bool _inFlight;                   // single-flight guard: at most one probe at a time
    private CancellationTokenSource? _inFlightCts; // cancels the current attempt (e.g. on disable)
    private int _enableGeneration;            // bumped on every Enabled transition — stamps each attempt
    private string? _cachedVersion;          // resolved, cached; re-resolved daily / on UA-changed (P1-3)
    private DateTimeOffset? _versionResolvedAt; // when _cachedVersion was last resolved (drives the daily refresh)
    private volatile bool _enabled;           // the runtime kill switch (T32)

    /// <param name="probeRunner">Probe-execution seam (production: <see cref="ProcessClaudeProbeRunner"/>; tests: a fake — never the real exe).</param>
    /// <param name="versionSource">Version-resolution seam (production: <see cref="ClaudeVersionResolver"/>; tests: a fake).</param>
    /// <param name="timeProvider">Clock seam for the gate, backoff, and snapshot timestamps — never <see cref="DateTimeOffset.UtcNow"/> directly.</param>
    /// <param name="enabled">Initial opt-in state (DESIGN.md §4.2, T32 — defaults OFF). Toggle at runtime via <see cref="Enabled"/>.</param>
    /// <param name="gateStore">
    /// Persistence seam for the cadence gate (P1-2). Defaults to a best-effort JSON file under
    /// <c>%LOCALAPPDATA%\AIUsage</c>; tests inject an in-memory fake so they never touch shared disk.
    /// </param>
    public ClaudeProvider(
        IClaudeProbeRunner probeRunner,
        IClaudeVersionSource versionSource,
        TimeProvider timeProvider,
        bool enabled = false,
        IClaudeGateStore? gateStore = null)
    {
        ArgumentNullException.ThrowIfNull(probeRunner);
        ArgumentNullException.ThrowIfNull(versionSource);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _probeRunner = probeRunner;
        _versionSource = versionSource;
        _timeProvider = timeProvider;
        _enabled = enabled;
        _gateStore = gateStore ?? new FileClaudeGateStore(FileClaudeGateStore.DefaultPath());

        LoadGateFromStore();
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public TimeSpan MinInterval => DefaultMinInterval;

    /// <summary>
    /// The opt-in kill switch (DESIGN.md §4.2, T32). Defaults OFF. Toggleable at runtime: disabled →
    /// every <see cref="FetchAsync"/> returns <c>Unavailable("disabled")</c> without resolving a version
    /// or spawning a probe, and any in-flight attempt is cancelled so its result cannot publish over the
    /// disabled snapshot.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_gate)
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                // Every transition invalidates any in-flight attempt: a result whose generation no longer
                // matches is discarded on completion (a late Ok can't overwrite "disabled").
                _enableGeneration++;

                if (!value)
                {
                    // Cancel the running probe (if any) under the lock. The only cancellation callback is the
                    // probe's own completion source (no _gate), and the awaited continuation resumes
                    // asynchronously — so this cannot re-enter the lock or deadlock.
                    _inFlightCts?.Cancel();
                }
            }
        }
    }

    /// <summary>
    /// The instant the next probe is eligible — the persisted wall reservation (DESIGN.md §4.2, T27). Before
    /// it, a fetch is coalesced (returns the last snapshot, spawns nothing). Observable for the footer's
    /// "next attempt" display and for tests. Note: the authoritative in-process gate is the MONOTONIC floor;
    /// this wall value can lag under a clock adjustment but only ever gates MORE conservatively.
    /// </summary>
    public DateTimeOffset NextEligibleAt
    {
        get
        {
            lock (_gate)
            {
                return new DateTimeOffset(_nextEligibleUtcTicks, TimeSpan.Zero);
            }
        }
    }

    /// <inheritdoc />
    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        // Kill switch first: disabled → honest n/a with NO version resolve and NO spawn (T32). Read outside
        // the lock (a volatile bool) so a disabled provider never even contends on the gate.
        if (!_enabled)
        {
            return BuildUnavailable(_timeProvider.GetUtcNow(), "disabled");
        }

        string version;
        int generation;
        CancellationTokenSource attemptCts;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();

            // Self-gate + coalescing (T27): before the gate opens, OR while a probe is already in flight,
            // return the last published snapshot unchanged — spawn nothing.
            if (_inFlight || !GateOpenLocked(now))
            {
                // _last is non-null on every realistic gated call in-process (the first fetch is never
                // gated). On a restart with a persisted, still-closed gate _last is null — the fallback is
                // the honest "no observation yet" n/a. Its reason is "gated" (waiting out the cadence gate,
                // nothing FAILED), NOT "fetch-error" — the latter would falsely imply a failed fetch on the
                // first post-restart tick (review NEW-3). Never a fabricated reading either way.
                return _last ?? BuildUnavailable(now, "gated");
            }

            // Re-check the switch under the gate (it may have flipped while another call held it).
            if (!_enabled)
            {
                return BuildUnavailable(now, "disabled");
            }

            // Version (T23): resolve once, cache in-provider; null → n/a("no-version") WITHOUT spawning.
            // Never fabricate. Re-resolve OPPORTUNISTICALLY (P1-3): daily, and on the UA-changed state
            // (which clears the cache). A daily re-resolve MISS keeps the last good version — never
            // downgrade a working UA to "no-version".
            if (_cachedVersion is null)
            {
                var resolved = _versionSource.Resolve();
                if (resolved is null)
                {
                    return CompleteLocked(BuildUnavailable(now, "no-version"), now, DefaultMinInterval);
                }

                _cachedVersion = resolved.Version;
                _versionResolvedAt = now;
            }
            else if (_versionResolvedAt is null || now - _versionResolvedAt.Value >= VersionRefreshInterval)
            {
                var resolved = _versionSource.Resolve();
                if (resolved is not null)
                {
                    _cachedVersion = resolved.Version;
                }

                _versionResolvedAt = now;
            }

            version = _cachedVersion;
            generation = _enableGeneration;

            // Reserve the cadence BEFORE launching (sol P1): a cancelled or indeterminate attempt then still
            // holds the gate — the probe may already have sent the request. A completed outcome OVERWRITES
            // this reservation in MapOutcomeLocked with the outcome-specific next-eligible instant.
            var reservationSpan = _backoffIndex >= 0 ? BackoffLadder[_backoffIndex] : DefaultMinInterval;
            SetGateLocked(now, reservationSpan);

            attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _inFlightCts = attemptCts;
            _inFlight = true;
        }

        try
        {
            // Run the probe OUTSIDE the lock (it is async I/O). At most one runs at a time — the _inFlight
            // guard above turns every concurrent trigger into a coalesced last-snapshot return.
            ClaudeProbeResult probe;
            try
            {
                probe = await _probeRunner.RunAsync(version, attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _inFlight = false;
                    ClearInFlightCtsLocked(attemptCts);
                    // The reservation set before launch is RETAINED (never cleared on cancel/indeterminate),
                    // so the next trigger cannot immediately fire another real request.
                }

                // A genuine shutdown (the CALLER's token) propagates; a disable-triggered cancel does not —
                // it resolves to an honest "disabled" so a partial attempt never surfaces a fabricated state.
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return BuildUnavailable(_timeProvider.GetUtcNow(), "disabled");
            }
            catch (Exception)
            {
                // A seam that throws unexpectedly is a transport failure, never a crash. The real probe
                // runner maps its own faults to exit codes; this is the belt-and-braces backstop.
                probe = new ClaudeProbeResult(ClaudeProbeExitCodes.Transport, null);
            }

            lock (_gate)
            {
                _inFlight = false;
                ClearInFlightCtsLocked(attemptCts);
                var receivedAt = _timeProvider.GetUtcNow();

                // Disable cancels in-flight (sol P1): if the enable-generation changed — or the provider is
                // now disabled — during the attempt, DISCARD the result so a late Ok can't publish over the
                // disabled snapshot. The reservation stands (retained) either way.
                if (generation != _enableGeneration || !_enabled)
                {
                    return BuildUnavailable(receivedAt, "disabled");
                }

                return MapOutcomeLocked(probe, receivedAt);
            }
        }
        finally
        {
            attemptCts.Dispose();
        }
    }

    /// <summary>
    /// Map one probe outcome to a snapshot and update the gate/backoff (DESIGN.md §4.2). Called under
    /// <see cref="_gate"/>. Every non-429 outcome resets the consecutive-429 counter.
    /// </summary>
    private ProviderSnapshot MapOutcomeLocked(ClaudeProbeResult probe, DateTimeOffset now)
    {
        switch (probe.ExitCode)
        {
            case ClaudeProbeExitCodes.Ok:
            {
                _consecutiveThrottle = 0;
                var parsed = ClaudeUsageParser.Parse(probe.StdoutJson, now);
                if (parsed.IsDrift)
                {
                    // Untrustable envelope → ALL Claude metrics n/a("source-changed"), backoff to cap
                    // (DESIGN.md §5). The one-time notification is the toast layer's job (M4).
                    return CompleteLocked(BuildUnavailable(now, "source-changed"), now, BackoffToCapLocked());
                }

                // Success → reset backoff to the 180s base.
                _backoffIndex = -1;
                var snapshot = new ProviderSnapshot(
                    ProviderId,
                    now,
                    SourceStatus.Ok,
                    StatusReasonCode: null,
                    Windows: parsed.UsageWindows,
                    CreditsBalance: parsed.Credits,
                    PlanType: parsed.PlanType);
                return CompleteLocked(snapshot, now, DefaultMinInterval);
            }

            case ClaudeProbeExitCodes.Throttled:
            {
                _consecutiveThrottle++;
                if (_consecutiveThrottle >= MaxConsecutiveThrottleBeforeDiagnostic)
                {
                    // A 429 can mean UA/endpoint rejection, which never clears by waiting — stop presenting
                    // "throttled" and surface the distinct diagnostic state, held at the 60-min cap (T28).
                    // Self-heal (P1-3): drop the cached version so the next eligible attempt re-resolves the
                    // UA — if the fixed version is on disk, the stuck state heals without a restart.
                    _cachedVersion = null;
                    _versionResolvedAt = null;
                    return CompleteLocked(BuildUnavailable(now, "endpoint-or-UA-changed"), now, BackoffToCapLocked());
                }

                return CompleteLocked(BuildUnavailable(now, "throttled"), now, AdvanceBackoffLocked());
            }

            case ClaudeProbeExitCodes.AuthRejected:
            {
                _consecutiveThrottle = 0;
                // Stop normal polling — a long backoff (cap) is the interim for T29's credential re-arm.
                // TODO(T29): watch the credentials file's PARENT DIRECTORY (atomic replace / absent-at-start)
                // and a single 401 re-read + retry within the same gated attempt, to re-arm before the cap.
                return CompleteLocked(BuildUnavailable(now, "auth-rejected"), now, BackoffToCapLocked());
            }

            case ClaudeProbeExitCodes.Credentials:
            {
                _consecutiveThrottle = 0;
                // No signed-in credential yet — walk the ladder; it may appear when the user opens Claude Code.
                // TODO(T29): re-arm on a credential-file appearance rather than only walking the ladder.
                return CompleteLocked(BuildUnavailable(now, "no-credentials"), now, AdvanceBackoffLocked());
            }

            case ClaudeProbeExitCodes.Schema:
            {
                _consecutiveThrottle = 0;
                // The probe saw a 200 but an untrustable body → source changed; backoff to cap (DESIGN.md §5).
                return CompleteLocked(BuildUnavailable(now, "source-changed"), now, BackoffToCapLocked());
            }

            case ClaudeProbeExitCodes.Transport:
            default:
            {
                // Transport/timeout/3xx, or any unexpected code (incl. a should-never-happen usage/arg error)
                // → a transient fetch error; walk the ladder.
                _consecutiveThrottle = 0;
                return CompleteLocked(BuildUnavailable(now, "fetch-error"), now, AdvanceBackoffLocked());
            }
        }
    }

    /// <summary>Advance one rung down the backoff ladder (saturating at the 60-min cap) and return the step span.</summary>
    private TimeSpan AdvanceBackoffLocked()
    {
        _backoffIndex = Math.Min(_backoffIndex + 1, BackoffLadder.Length - 1);
        return BackoffLadder[_backoffIndex];
    }

    /// <summary>Jump straight to the 60-min cap (for non-transient failures: auth, schema drift, UA-rejection).</summary>
    private TimeSpan BackoffToCapLocked()
    {
        _backoffIndex = BackoffLadder.Length - 1;
        return BackoffLadder[_backoffIndex];
    }

    private ProviderSnapshot CompleteLocked(ProviderSnapshot snapshot, DateTimeOffset now, TimeSpan span)
    {
        _last = snapshot;
        SetGateLocked(now, span);
        return snapshot;
    }

    /// <summary>
    /// Set the next-eligible instant, updating BOTH the persisted wall reservation and the in-process
    /// monotonic floor, and persist it best-effort. Called under <see cref="_gate"/>.
    /// </summary>
    private void SetGateLocked(DateTimeOffset now, TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        _nextEligibleUtcTicks = (now + span).UtcTicks;
        _monotonicEligibleTimestamp = _timeProvider.GetTimestamp() + SpanToTimestampTicks(span);
        _hasMonotonicFloor = true;
        PersistGateLocked();
    }

    /// <summary>
    /// Is the gate open? Once this process has reserved at least once, the MONOTONIC floor is authoritative
    /// — immune to wall-clock jumps in either direction. Before the first in-process reservation (i.e. right
    /// after a restart), the persisted wall value governs — the ONLY case it does.
    /// </summary>
    private bool GateOpenLocked(DateTimeOffset now)
    {
        if (_hasMonotonicFloor)
        {
            return _timeProvider.GetTimestamp() >= _monotonicEligibleTimestamp;
        }

        return now.UtcTicks >= _nextEligibleUtcTicks;
    }

    private long SpanToTimestampTicks(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return 0;
        }

        // Convert a duration into TimeProvider.GetTimestamp() units. Spans here are <= 60 min, so the double
        // product is exact (well within 2^53) for any realistic TimestampFrequency.
        return (long)(span.TotalSeconds * _timeProvider.TimestampFrequency);
    }

    private void PersistGateLocked()
    {
        // Best-effort — mirrors the version-cache pattern. The store swallows every I/O fault, so this can
        // never throw out of a fetch.
        _gateStore.Save(new ClaudeGateState(
            new DateTimeOffset(_nextEligibleUtcTicks, TimeSpan.Zero),
            _backoffIndex,
            _consecutiveThrottle));
    }

    private void LoadGateFromStore()
    {
        var state = _gateStore.Load();
        if (state is null)
        {
            return; // missing / corrupt → fresh gate (the first fetch runs immediately)
        }

        var now = _timeProvider.GetUtcNow();
        var cap = now + BackoffLadder[^1]; // clamp: at most now + 60 min, so a corrupt far-future value can't wedge us
        var next = state.NextEligibleAt;
        if (next > cap)
        {
            next = cap;
        }

        _nextEligibleUtcTicks = next.UtcTicks;
        _backoffIndex = Math.Clamp(state.BackoffIndex, -1, BackoffLadder.Length - 1);
        _consecutiveThrottle = state.ConsecutiveThrottle < 0 ? 0 : state.ConsecutiveThrottle;
        // Deliberately leave _hasMonotonicFloor false: there is no in-process monotonic history across a
        // restart, so the persisted wall value gates the first post-restart attempt (its only role).
    }

    private void ClearInFlightCtsLocked(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_inFlightCts, cts))
        {
            _inFlightCts = null;
        }
    }

    private static ProviderSnapshot BuildUnavailable(DateTimeOffset now, string reasonCode) => new(
        ProviderId: ProviderId,
        FetchedAt: now,
        Status: SourceStatus.Unavailable,
        StatusReasonCode: reasonCode,
        Windows: Array.Empty<UsageWindow>(),
        CreditsBalance: Metric.Unavailable<decimal>(reasonCode),
        PlanType: Metric.Unavailable<string>(reasonCode));
}

/// <summary>
/// The persisted cadence-gate state (P1-2; DESIGN.md §2 "rate-control metadata ... next eligible attempt").
/// Only rate-control metadata is persisted — never a usage reading (the persistence floor stays intact).
/// </summary>
/// <param name="NextEligibleAt">The reserved next-eligible instant, in UTC.</param>
/// <param name="BackoffIndex">Ladder position: <c>-1</c> = base cadence, <c>0..</c> = a ladder rung.</param>
/// <param name="ConsecutiveThrottle">Consecutive-429 count, so the 5×429 diagnostic survives a restart.</param>
public sealed record ClaudeGateState(DateTimeOffset NextEligibleAt, int BackoffIndex, int ConsecutiveThrottle);

/// <summary>
/// The persistence SEAM for the Claude cadence gate (P1-2). Abstracted so <see cref="ClaudeProvider"/> is
/// unit-testable against an in-memory fake (never shared disk); production wires
/// <see cref="FileClaudeGateStore"/>. Every method is best-effort and non-throwing — a persistence fault
/// must never fail a fetch (DESIGN.md §4.2).
/// </summary>
public interface IClaudeGateStore
{
    /// <summary>Load the persisted gate, or <c>null</c> when absent/corrupt. Never throws.</summary>
    ClaudeGateState? Load();

    /// <summary>Persist the gate, best-effort. Swallows every I/O fault — never throws.</summary>
    void Save(ClaudeGateState state);
}

/// <summary>
/// The production <see cref="IClaudeGateStore"/>: a best-effort JSON file under
/// <c>%LOCALAPPDATA%\AIUsage\claude-gate.json</c> (P1-2). Same containment as the version cache — a
/// missing/corrupt file loads as "fresh", and every write/read fault is swallowed.
/// </summary>
public sealed class FileClaudeGateStore : IClaudeGateStore
{
    /// <summary>The gate file name under <see cref="ClaudeVersionPaths.AppDataFolderName"/>.</summary>
    public const string GateFileName = "claude-gate.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    /// <param name="path">Absolute path to the gate file.</param>
    public FileClaudeGateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The absolute gate-file path this store reads/writes.</summary>
    public string Path => _path;

    /// <summary>The production path: <c>%LOCALAPPDATA%\AIUsage\claude-gate.json</c> (never roaming).</summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ClaudeVersionPaths.AppDataFolderName,
        GateFileName);

    /// <inheritdoc />
    public ClaudeGateState? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<GateDto>(File.ReadAllText(_path), JsonOptions);
            if (dto?.NextEligibleAt is null)
            {
                return null;
            }

            return new ClaudeGateState(dto.NextEligibleAt.Value, dto.BackoffIndex, dto.ConsecutiveThrottle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // A corrupt/unreadable gate file is a miss, never an error — the provider starts fresh.
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(ClaudeGateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = new GateDto
            {
                NextEligibleAt = state.NextEligibleAt,
                BackoffIndex = state.BackoffIndex,
                ConsecutiveThrottle = state.ConsecutiveThrottle,
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best-effort — a failed persist must never fail the fetch that produced the state.
        }
    }

    /// <summary>The serialised gate shape. Nullable <see cref="NextEligibleAt"/> so a partial file loads as a miss.</summary>
    private sealed class GateDto
    {
        public DateTimeOffset? NextEligibleAt { get; set; }
        public int BackoffIndex { get; set; }
        public int ConsecutiveThrottle { get; set; }
    }
}
