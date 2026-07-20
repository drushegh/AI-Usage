namespace AIUsage.Core.Tests;

/// <summary>
/// A fake <see cref="IClaudeProbeRunner"/> for the provider tests: returns canned
/// <see cref="ClaudeProbeResult"/>s WITHOUT ever spawning the real exe or touching the network. It
/// yields <c>results[0], results[1], …</c> then repeats the LAST one, so a single result drives the
/// "always this outcome" tests (ladder, 429 storm) while a sequence drives fail→success transitions.
/// Records the call count and the last version passed so a test can assert the probe was (or was not)
/// invoked and that the resolved version reached the argv.
/// </summary>
internal sealed class FakeClaudeProbeRunner : IClaudeProbeRunner
{
    private readonly IReadOnlyList<ClaudeProbeResult> _results;

    public FakeClaudeProbeRunner(params ClaudeProbeResult[] results)
    {
        _results = results.Length > 0
            ? results
            : new[] { new ClaudeProbeResult(ClaudeProbeExitCodes.Transport, null) };
    }

    /// <summary>How many times <see cref="RunAsync"/> has been invoked (i.e. probes actually spawned).</summary>
    public int Calls { get; private set; }

    /// <summary>The version string passed to the most recent run — proves the resolved version reached the probe.</summary>
    public string? LastVersion { get; private set; }

    public Task<ClaudeProbeResult> RunAsync(string claudeVersion, CancellationToken cancellationToken)
    {
        LastVersion = claudeVersion;
        var index = Math.Min(Calls, _results.Count - 1);
        Calls++;
        return Task.FromResult(_results[index]);
    }
}

/// <summary>
/// A fake <see cref="IClaudeProbeRunner"/> whose single run BLOCKS until the test completes it, so a test
/// can act (disable, cancel) while an attempt is genuinely in flight. <see cref="Started"/> signals once
/// the run has been entered. With <c>honorCancellation</c> the run cancels when its token fires (the probe
/// aborting); without it the run ignores cancellation and completes only when <see cref="Complete"/> is
/// called (the "late Ok" race that the enable-generation guard must discard).
/// </summary>
internal sealed class BlockingClaudeProbeRunner : IClaudeProbeRunner
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ClaudeProbeResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _honorCancellation;

    public BlockingClaudeProbeRunner(bool honorCancellation = true) => _honorCancellation = honorCancellation;

    /// <summary>Completes once <see cref="RunAsync"/> has been entered (an attempt is in flight).</summary>
    public Task Started => _started.Task;

    /// <summary>How many times a run was started.</summary>
    public int Calls { get; private set; }

    /// <summary>The version passed to the run.</summary>
    public string? LastVersion { get; private set; }

    /// <summary>Complete the in-flight run with <paramref name="result"/> (ignored if already cancelled).</summary>
    public void Complete(ClaudeProbeResult result) => _result.TrySetResult(result);

    public async Task<ClaudeProbeResult> RunAsync(string claudeVersion, CancellationToken cancellationToken)
    {
        Calls++;
        LastVersion = claudeVersion;
        _started.TrySetResult();

        if (_honorCancellation)
        {
            await using var registration = cancellationToken.Register(() => _result.TrySetCanceled(cancellationToken));
            return await _result.Task.ConfigureAwait(false);
        }

        return await _result.Task.ConfigureAwait(false);
    }
}

/// <summary>
/// A fake <see cref="IClaudeVersionSource"/>: returns a settable <see cref="ClaudeVersionResult"/> (or
/// <c>null</c> for the no-version path) WITHOUT any filesystem or process spawn. The result is MUTABLE via
/// <see cref="SetResult"/> so a test can model a version changing on disk (self-heal / daily re-resolve).
/// Records the call count so a test can assert the version was — or was not — resolved.
/// </summary>
internal sealed class FakeClaudeVersionSource : IClaudeVersionSource
{
    private ClaudeVersionResult? _result;

    public FakeClaudeVersionSource(ClaudeVersionResult? result) => _result = result;

    /// <summary>How many times <see cref="Resolve"/> was called.</summary>
    public int Calls { get; private set; }

    /// <summary>Change what the next <see cref="Resolve"/> returns (models a new version landing on disk).</summary>
    public void SetResult(ClaudeVersionResult? result) => _result = result;

    public ClaudeVersionResult? Resolve()
    {
        Calls++;
        return _result;
    }
}

/// <summary>
/// An in-memory <see cref="IClaudeGateStore"/> for the provider tests: never touches shared disk. Optionally
/// pre-seeded (to model a persisted gate surviving a "restart"), and records every save so a test can assert
/// the gate was persisted whenever it advanced.
/// </summary>
internal sealed class FakeClaudeGateStore : IClaudeGateStore
{
    private ClaudeGateState? _state;

    public FakeClaudeGateStore(ClaudeGateState? initial = null) => _state = initial;

    /// <summary>How many times <see cref="Save"/> has been called (the gate advancing).</summary>
    public int Saves { get; private set; }

    /// <summary>The last-saved state (the most recent persisted gate).</summary>
    public ClaudeGateState? Saved => _state;

    public ClaudeGateState? Load() => _state;

    public void Save(ClaudeGateState state)
    {
        _state = state;
        Saves++;
    }
}
