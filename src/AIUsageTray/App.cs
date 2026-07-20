using System.Windows;
using System.Windows.Threading;
using AIUsage.Core;
using Microsoft.Win32;

// This project enables both UseWPF and UseWindowsForms, so `Application` is
// ambiguous (System.Windows.Application vs the implicit System.Windows.Forms.Application
// global using). Alias it to the WPF type this file uses exclusively.
using WpfApplication = System.Windows.Application;

namespace AIUsageTray;

/// <summary>
/// The WPF application. Deliberately has NO main window and NO StartupUri: this is a tray app. On
/// startup it wires the Core engine (DESIGN.md §4) into the live tray: one shared
/// <see cref="SnapshotStore"/> that the registered providers publish into, a
/// <see cref="LastKnownReadingStore"/> for DATED history, and a <see cref="TrayIconController"/> that
/// renders each <see cref="UsageView"/>. This is the Codex-only shell — only <see cref="CodexProvider"/>
/// is registered.
/// </summary>
/// <remarks>
/// <b>Threading (DESIGN.md §4).</b> <see cref="SnapshotStore.SnapshotChanged"/> fires on the provider
/// loop's background thread. Every handler here marshals to the WPF dispatcher before touching the
/// store-derived view or the tray, so all UI mutation happens on the one dispatcher thread.
/// </remarks>
public sealed class App : WpfApplication
{
    private readonly SnapshotStore _store = new();
    private readonly LastKnownReadingStore _lastKnown = new();
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly AppConfigStore _appConfigStore = new();
    private readonly IAutostart _autostart = Autostart.ForCurrentUser();

    // The live display config (thresholds + Codex/Claude TTLs) the icon renderer, notification decider, and
    // UsageViewBuilder all read. Seeded from DisplayConfig.Default, then REPLACED in OnStartup with the
    // persisted settings and again whenever the Settings window applies new values (T41). Only read/written
    // on the dispatcher thread.
    private DisplayConfig _config = DisplayConfig.Default;

    // The pure notification engine (T36/T37): fed each freshly built UsageView + now on the dispatcher
    // thread, it returns the toasts to fire this tick. Shares the SAME DisplayConfig thresholds the icon
    // severity uses, so a threshold toast and the icon warning band can never disagree. Rebuilt whenever the
    // thresholds change (Settings apply) so the two stay in lockstep.
    private NotificationDecider _notifications = new(DisplayConfig.Default);

    private ProviderHost? _host;
    private CodexProvider? _codex;
    private ClaudeProvider? _claude;
    private AppConfig _appConfig = AppConfig.Default;
    private TrayIconController? _tray;
    private INotifier? _notifier;
    private UsagePopup? _popup;
    private SettingsWindow? _settings;
    private UsageView? _currentView;

    // Independent per-provider manual-refresh guards (review P2-5): a wedged Codex rescan must never block a
    // Claude refresh, or vice-versa — so each provider self-coalesces on its own flag rather than one shared
    // one, and each fetch is time-bounded so a hung probe/FS read can't disable "Refresh now" for the session.
    private int _codexRefreshInFlight;
    private int _claudeRefreshInFlight;

    // Coarse re-classification timer (review P2-8): rebuilds the view from the CURRENT store every ~10s (the
    // builder is pure + cheap) so a TTL expiry or a passed reset flips LIVE → DATED/n-a promptly without
    // waiting for the next publish. Also re-run on resume-from-sleep via SystemEvents.PowerModeChanged.
    private DispatcherTimer? _recomputeTimer;

    // Second-launch activation channel (review P2-18): the primary owns this named auto-reset event and
    // listens on it; a second launch signals it (Program) so the running tray shows its popup instead of the
    // second process only flashing a MessageBox.
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateRegistration;

    // Consecutive dispatcher faults handled by the backstop; after a streak we degrade the icon to "?".
    private int _dispatcherFaultStreak;

    // False until OnStartup has finished wiring the tray/store/host. Until then the dispatcher backstop must
    // NOT swallow a fault (review NEW-1): a half-initialised app that "handled" a startup GDI throw would keep
    // the single-instance mutex while showing no icon/store/providers, and a relaunch would only hit the
    // "already running" box — the precise silent-absence-that-also-blocks-relaunch failure the backstop exists
    // to kill. A pre-completion fault is therefore treated as fatal → Shutdown(1) so the mutex releases.
    private bool _startupCompleted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No window is ever shown, so the default OnLastWindowClose would never fire (there is no last
        // window). Shut down only when Exit is chosen.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Guarded popup self-test (never on the shipping path): construct + render the popup off-screen
        // with a sample view so a layout/binding error surfaces at startup, then exit with a status code.
        if (string.Equals(Environment.GetEnvironmentVariable("AIUSAGE_SELFTEST"), "1", StringComparison.Ordinal))
        {
            RunPopupSelfTestAndExit();
            return;
        }

        // Guarded settings-window self-test (never on the shipping path): construct + lay out the Settings
        // window off-screen so a layout error surfaces at startup, then exit with a status code.
        if (string.Equals(Environment.GetEnvironmentVariable("AIUSAGE_SELFTEST"), "settings", StringComparison.Ordinal))
        {
            RunSettingsSelfTestAndExit();
            return;
        }

        // Global exception backstop (review P1-4). A monitoring app must NEVER die silently: a transient GDI
        // fault inside a posted dispatcher op (RefreshView → icon/tooltip/popup rebuild) is otherwise an
        // unhandled WPF exception → the process exits with no window and the icon just vanishes. Subscribe on
        // the real path only (the self-tests above have their own try/catch + exit codes).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Load the persisted config. Seeds the provider kill switch, the menu's checked state, AND the live
        // display config (thresholds + Codex TTL) so disk, provider, and UI agree from the first frame — a
        // saved 85/95 threshold or a tuned TTL survives restart.
        _appConfig = _appConfigStore.Load();
        _config = _appConfig.ToDisplayConfig();
        _notifications = new NotificationDecider(_config);

        _tray = new TrayIconController(
            onExit: OnExitRequested,
            onRefresh: OnRefreshRequested,
            onOpen: OnTogglePopup,
            onToggleClaude: OnToggleClaude,
            getClaudeEnabled: () => _appConfig.ClaudeEnabled,
            onSettings: OnSettingsRequested,
            getStartWithWindows: _autostart.IsEnabled,
            onToggleStartWithWindows: OnToggleStartWithWindows);

        // The production notification sink: decided notifications become WinForms tray balloons via the
        // controller that owns the icon (BalloonNotifier documents the E6 mechanism choice). Behind the
        // INotifier seam so it stays swappable.
        _notifier = new BalloonNotifier(_tray);

        // Subscribe BEFORE the host starts so the very first published snapshot repaints the tray.
        _store.SnapshotChanged += OnSnapshotChanged;

        _codex = new CodexProvider(_clock);

        // Claude collector (T31/T32): the real remote, opt-in provider. It never touches the network itself —
        // ProcessClaudeProbeRunner spawns ClaudeUsageProbe.exe (staged next to this exe by the build) and
        // ClaudeVersionResolver resolves the Claude Code version for the probe's User-Agent. Enabled is seeded
        // from the persisted flag and defaults OFF; both providers publish into the one shared store, so the
        // UsageView aggregates them with no further wiring.
        _claude = new ClaudeProvider(
            new ProcessClaudeProbeRunner(),
            new ClaudeVersionResolver(
                ClaudeVersionPaths.ForCurrentMachine(),
                new SystemVersionFileSystem(),
                new ProcessClaudeVersionCommandRunner(),
                _clock),
            _clock,
            enabled: _appConfig.ClaudeEnabled);

        _host = new ProviderHost(_store, _clock);
        _host.Register(_codex);
        _host.Register(_claude);
        _host.Start();

        // Paint the initial state immediately (nothing published yet → all-unknown "?").
        RefreshView();

        // Coarse re-classification timer + resume-from-sleep invalidation (review P2-8). The view is otherwise
        // recomputed only on SnapshotChanged, so a stale LIVE reading (after laptop sleep) or a reset that
        // passes mid-interval would linger up to a publish interval. Both re-run RefreshView, which rebuilds
        // the (pure, cheap) view from the current store — no new fetch.
        _recomputeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _recomputeTimer.Tick += (_, _) => RefreshView();
        _recomputeTimer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Listen for a second launch so it activates this instance's popup (review P2-18) instead of the
        // second process only showing a MessageBox.
        _activateSignal = CreateActivationListener();

        // First-run only: point the owner at the Win11 overflow flyout so the glanceable premise doesn't
        // silently fail (T40). Shown once, then persisted off.
        MaybeShowFirstRunHint();

        // Wiring is complete (review NEW-1): from here a transient dispatcher fault may be swallowed and the
        // tray kept alive. BEFORE this point the backstop treats any fault as fatal (see the handler) so a
        // half-initialised, mutex-holding zombie can never survive startup.
        _startupCompleted = true;
    }

    /// <summary>
    /// Show the one-time first-run overflow-pin hint (DESIGN.md §7 Windows integration 1; task T40): on
    /// Win11 a new tray icon lands hidden in the overflow (^) flyout and cannot be promoted
    /// programmatically, so the owner must drag it out. Shown exactly once per install — the
    /// <see cref="AppConfig.FirstRunShown"/> flag is flipped and persisted so it never repeats. Runs on the
    /// dispatcher thread (called from <see cref="OnStartup"/>).
    /// </summary>
    private void MaybeShowFirstRunHint()
    {
        if (_appConfig.FirstRunShown || _tray is null)
        {
            return;
        }

        _tray.ShowBalloon(
            "AI-Usage is running",
            "Windows 11 may hide the icon in the overflow (^) flyout — drag it onto the taskbar to keep it visible.",
            System.Windows.Forms.ToolTipIcon.Info);

        _appConfig = _appConfig with { FirstRunShown = true };
        _appConfigStore.Save(_appConfig);
    }

    /// <summary>
    /// Rebuild the render model from the store + retained history and repaint the tray. Runs on the UI
    /// (dispatcher) thread only.
    /// </summary>
    private void RefreshView()
    {
        var tray = _tray;
        if (tray is null)
        {
            return;
        }

        var snapshots = _store.Snapshots;
        foreach (var snapshot in snapshots.Values)
        {
            _lastKnown.RecordFrom(snapshot);
        }

        var view = UsageViewBuilder.Build(snapshots, _lastKnown, _config, _clock);
        _currentView = view;
        tray.Update(view);

        // A completed repaint clears the dispatcher-fault streak (review NEW-2): the streak counts CONSECUTIVE
        // handled faults since the last good paint, not a lifetime total — so a later transient blip must again
        // reach the threshold before the icon is force-degraded to the all-unknown "?".
        _dispatcherFaultStreak = 0;

        // Evaluate notifications on the same dispatcher tick that painted the icon (T36/T37): threshold
        // crossings from LIVE readings and sustained provider outages. The decider is pure/stateful; firing
        // each returned request through the swappable INotifier keeps the timing anchored to _clock.
        FireNotifications(view);

        // Live-update the popup while it is open (T15/T19): same dispatcher, same view instance the tray
        // just painted. Closed/hidden popups are skipped — nothing to repaint. A popup render fault is
        // contained here so it can never propagate and take the tray down (review P1-4).
        if (_popup is { IsAlive: true, IsVisible: true } popup)
        {
            try
            {
                popup.Update(view);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Popup update failed: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Feed the freshly built <see cref="UsageView"/> to the <see cref="NotificationDecider"/> and surface
    /// whatever it decides to fire this tick (T36/T37). Runs on the dispatcher thread only (the decider is
    /// single-threaded by contract), and "now" comes from the one injected <see cref="_clock"/> so the
    /// dwell/flap timing stays testable.
    /// </summary>
    private void FireNotifications(UsageView view)
    {
        var notifier = _notifier;
        if (notifier is null)
        {
            return;
        }

        foreach (var request in _notifications.Evaluate(view, _clock.GetUtcNow()))
        {
            notifier.Notify(request);
        }
    }

    /// <summary>
    /// Toggle the detail popup (T14) from a tray left-click. Lazily creates the window (recreating it only
    /// if a previous instance was really closed), then hands it the latest built <see cref="UsageView"/>.
    /// Runs on the dispatcher thread (the NotifyIcon click callback is delivered there).
    /// </summary>
    private void OnTogglePopup()
    {
        if (_popup is null || !_popup.IsAlive)
        {
            _popup = new UsagePopup(_clock, onRefresh: OnRefreshRequested, onExit: OnExitRequested);
        }

        _popup.Toggle(_currentView ?? EmptyView());
    }

    private static UsageView EmptyView()
        => new(Severity.Normal, Unknown: true, AllUnknown: true, Array.Empty<ProviderView>());

    private void OnSnapshotChanged(object? sender, SnapshotChangedEventArgs e)
    {
        // Fires on the provider loop's background thread — marshal every UI mutation to the dispatcher.
        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.InvokeAsync(RefreshView);
        }
        catch (TaskCanceledException)
        {
            // The dispatcher began shutting down between the guard and the post — nothing to repaint.
        }
    }

    /// <summary>
    /// Refresh now (T16): trigger an immediate rescan of BOTH providers — a Codex file rescan and a Claude
    /// fetch through its own 180s gate (a gated/disabled Claude simply re-publishes its last/honest state,
    /// spawning nothing). Each publish raises <see cref="SnapshotStore.SnapshotChanged"/>, which repaints
    /// through the normal path. One combined refresh is kept in flight at a time; each provider additionally
    /// self-coalesces, so this can never double-scan or double-spawn.
    /// </summary>
    private void OnRefreshRequested()
    {
        // Fire each provider's refresh INDEPENDENTLY (review P2-5): no shared in-flight flag and no
        // Task.WhenAll, so a wedged Codex rescan can never suppress a Claude refresh, or vice-versa. Each
        // method self-coalesces on its own guard and bounds its fetch, so a hung probe can't disable
        // "Refresh now" for the session.
        var codex = _codex;
        if (codex is not null)
        {
            _ = RefreshCodexAsync(codex);
        }

        var claude = _claude;
        if (claude is not null)
        {
            _ = RefreshClaudeAsync(claude);
        }
    }

    private async Task RefreshCodexAsync(CodexProvider codex)
    {
        if (Interlocked.Exchange(ref _codexRefreshInFlight, 1) == 1)
        {
            return; // a Codex rescan is already running; coalesce this request into it
        }

        try
        {
            await RefreshBoundedAsync(CodexProvider.ProviderId, () => codex.FetchAsync(CancellationToken.None));
        }
        finally
        {
            Interlocked.Exchange(ref _codexRefreshInFlight, 0);
        }
    }

    private async Task RefreshClaudeAsync(ClaudeProvider claude)
    {
        if (Interlocked.Exchange(ref _claudeRefreshInFlight, 1) == 1)
        {
            return; // a Claude rescan is already running; coalesce this request into it
        }

        try
        {
            // The provider's internal gate + kill switch decide whether this spawns a probe, returns the last
            // snapshot, or returns Unavailable("disabled") — either way it yields a snapshot we publish.
            await RefreshBoundedAsync(ClaudeProvider.ProviderId, () => claude.FetchAsync(CancellationToken.None));
        }
        finally
        {
            Interlocked.Exchange(ref _claudeRefreshInFlight, 0);
        }
    }

    /// <summary>
    /// Run one manual fetch BOUNDED by <see cref="ProviderRunner.DefaultFetchTimeout"/> (review P2-5). The
    /// fetch runs on a worker (Codex/Claude do synchronous work before their first await); on timeout the
    /// orphaned fetch is observed (so it can't surface as an unobserved-task exception), an honest
    /// <c>refresh-error</c> is published so the icon never reads stale-as-fresh, and the caller releases its
    /// guard — the provider's own runner loop then recovers on its normal cadence. Never throws.
    /// </summary>
    private async Task RefreshBoundedAsync(string providerId, Func<Task<ProviderSnapshot>> fetch)
    {
        var fetchTask = Task.Run(fetch);
        try
        {
            var snapshot = await fetchTask.WaitAsync(ProviderRunner.DefaultFetchTimeout);
            _store.Publish(snapshot);
        }
        catch (TimeoutException)
        {
            Observe(fetchTask); // the orphan may still fault/complete later — swallow its result
            System.Diagnostics.Debug.WriteLine($"{providerId} manual refresh timed out.");
            _store.Publish(RefreshError(providerId));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{providerId} refresh failed: {ex}");
            _store.Publish(RefreshError(providerId));
        }
    }

    /// <summary>Swallow a faulted orphan's exception so a timed-out fetch can never surface as unobserved.</summary>
    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private ProviderSnapshot RefreshError(string providerId) => new(
        providerId,
        _clock.GetUtcNow(),
        SourceStatus.Unavailable,
        "refresh-error",
        Array.Empty<UsageWindow>(),
        Metric.Unavailable<decimal>("refresh-error"),
        Metric.Unavailable<string>("refresh-error"));

    /// <summary>
    /// Toggle the Claude opt-in (T16/T32) from the tray menu's checkable item. Flips the provider's kill
    /// switch, persists the flag, and triggers a refresh so the change is visible immediately: enabling
    /// kicks a first fetch, disabling re-publishes an honest <c>Unavailable("disabled")</c> snapshot.
    /// Runs on the dispatcher thread (the menu Click is delivered there).
    /// </summary>
    private void OnToggleClaude(bool enabled)
    {
        var claude = _claude;
        if (claude is not null)
        {
            claude.Enabled = enabled;
        }

        _appConfig = _appConfig with { ClaudeEnabled = enabled };
        _appConfigStore.Save(_appConfig);

        OnRefreshRequested();
    }

    /// <summary>
    /// Open the owner-tunable Settings window (T41) from the tray menu. Single instance: an already-open
    /// window is re-activated rather than duplicated. Runs on the dispatcher thread (the menu Click is
    /// delivered there); the current autostart state is read from the registry HERE (a plain bool the
    /// window never has to touch the registry for).
    /// </summary>
    private void OnSettingsRequested()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        var window = new SettingsWindow(_appConfig, _autostart.IsEnabled(), ApplySettings);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settings, window))
            {
                _settings = null;
            }
        };
        _settings = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Apply the values the owner accepted in the Settings window (T41/T38). Rebuilds the live
    /// <see cref="DisplayConfig"/> from the new thresholds + Codex TTL and re-creates the
    /// <see cref="NotificationDecider"/> so the icon, notifications, and the next
    /// <see cref="UsageViewBuilder"/> build all use the new values; flips the Claude kill switch if it
    /// changed; writes/removes the HKCU Run autostart value; persists everything; then repaints LIVE and
    /// kicks a rescan. Runs on the dispatcher thread. Never throws — every persistence side-effect
    /// (config save, registry write) is already best-effort/swallowing.
    /// </summary>
    private void ApplySettings(SettingsResult result)
    {
        // Defensive clamp: the window validated, but a value must never reach the display engine unclamped.
        var updated = SettingsValidation.Clamp(_appConfig with
        {
            WarnPercent = result.WarnPercent,
            CritPercent = result.CritPercent,
            CodexTtlMinutes = result.CodexTtlMinutes,
            ClaudeEnabled = result.ClaudeEnabled,
        });
        _appConfig = updated;

        // Rebuild the live display config + notification thresholds (kept in lockstep with the icon).
        _config = updated.ToDisplayConfig();
        _notifications = new NotificationDecider(_config);

        // Flip the Claude collector's kill switch if it changed.
        if (_claude is not null)
        {
            _claude.Enabled = updated.ClaudeEnabled;
        }

        // Add/remove the sign-in autostart registration (best-effort — never fatal on the settings path).
        _autostart.SetEnabled(result.StartWithWindows);

        // Persist everything, then apply LIVE: repaint the icon with the new thresholds immediately, and
        // kick a rescan so a Claude enable/disable also refetches (its publish re-drives RefreshView).
        _appConfigStore.Save(updated);
        RefreshView();
        OnRefreshRequested();
    }

    /// <summary>
    /// Toggle the sign-in autostart registration from the tray menu's "Start with Windows" item (T38).
    /// Best-effort registry write via <see cref="Autostart"/>; never throws.
    /// </summary>
    private void OnToggleStartWithWindows(bool enabled) => _autostart.SetEnabled(enabled);

    /// <summary>
    /// The dispatcher-level exception backstop (review P1-4). A monitoring tray must never vanish: a non-fatal
    /// (transient GDI/render/timing) fault is logged token-free, marked handled, and after a streak the icon is
    /// degraded to the honest all-unknown "?" — a stale "safe" icon is worse than an explicit "cannot tell".
    /// A genuinely fatal (corrupted-state) fault is left unhandled so the process can fail rather than limp on.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Before startup wiring completes, a dispatcher fault must NOT be swallowed (review NEW-1): a
        // half-initialised app that "handled" it would keep the single-instance mutex while showing no
        // icon/store/providers, and a relaunch would only hit the "already running" box. Treat it as fatal —
        // mark it handled so WPF doesn't ALSO crash-terminate, then Shutdown(1) so the mutex releases and a
        // relaunch works, instead of limping on as a silent zombie.
        if (!_startupCompleted)
        {
            System.Diagnostics.Debug.WriteLine($"Startup dispatcher fault (fatal): {e.Exception.GetType().FullName}");
            e.Handled = true;
            Shutdown(1);
            return;
        }

        if (TrayFaultPolicy.IsFatal(e.Exception))
        {
            return; // leave e.Handled = false → the process terminates rather than continue in an unknown state
        }

        e.Handled = true;
        System.Diagnostics.Debug.WriteLine($"Dispatcher fault handled: {e.Exception.GetType().FullName}");

        if (++_dispatcherFaultStreak >= 3)
        {
            _tray?.ShowAllUnknown();
        }
    }

    /// <summary>Observe (and log) an unobserved Task exception so a background fault can never escalate.</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        System.Diagnostics.Debug.WriteLine($"Unobserved task exception observed: {e.Exception.GetType().FullName}");
    }

    /// <summary>Last-ditch breadcrumb: an AppDomain-level unhandled exception can't be stopped, but log it token-free.</summary>
    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var type = (e.ExceptionObject as Exception)?.GetType().FullName ?? "unknown";
        System.Diagnostics.Debug.WriteLine($"AppDomain unhandled ({(e.IsTerminating ? "terminating" : "non-terminating")}): {type}");
    }

    /// <summary>
    /// Re-run the view on resume-from-sleep (review P2-8): a stale LIVE reading (e.g. a laptop that slept for
    /// hours) or a reset that passed while asleep must be invalidated immediately, not at the next publish.
    /// Fires on the SystemEvents thread — marshal to the dispatcher.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.InvokeAsync(RefreshView);
        }
        catch (TaskCanceledException)
        {
        }
    }

    /// <summary>
    /// Create the named auto-reset event a second launch signals (review P2-18) and register a wait that shows
    /// the popup when signalled. Best-effort — a failure here just falls back to the old MessageBox path.
    /// </summary>
    private EventWaitHandle? CreateActivationListener()
    {
        try
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
            _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
                handle,
                (_, _) => OnActivateSignal(),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
            return handle;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activation listener setup failed: {ex}");
            return null;
        }
    }

    private void OnActivateSignal()
    {
        // Fires on a thread-pool thread when a second launch signals us — marshal to the dispatcher.
        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.InvokeAsync(ShowPopupForActivation);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void ShowPopupForActivation()
    {
        if (_popup is null || !_popup.IsAlive)
        {
            _popup = new UsagePopup(_clock, onRefresh: OnRefreshRequested, onExit: OnExitRequested);
        }

        if (_popup.IsVisible)
        {
            _popup.Activate();
        }
        else
        {
            _popup.Toggle(_currentView ?? EmptyView());
        }
    }

    /// <summary>
    /// Exit (T16): stop feeding the UI, wind the host down gracefully, then shut the app down (which
    /// disposes the tray in <see cref="OnExit"/>).
    /// </summary>
    private async void OnExitRequested()
    {
        _store.SnapshotChanged -= OnSnapshotChanged;

        var host = _host;
        _host = null;

        try
        {
            if (host is not null)
            {
                await host.StopAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Host stop during exit failed: {ex}");
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _store.SnapshotChanged -= OnSnapshotChanged;

        // Leak hygiene (review P2-8/P2-18): stop the coarse timer, drop the resume hook, and release the
        // activation channel before the shell tears the process down.
        _recomputeTimer?.Stop();
        _recomputeTimer = null;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _activateRegistration?.Unregister(null);
        _activateRegistration = null;
        _activateSignal?.Dispose();
        _activateSignal = null;

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;

        _settings?.CloseForReal();
        _settings = null;
        _popup?.CloseForReal();
        _popup = null;
        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }

    /// <summary>
    /// Construct and render the popup off-screen with a representative view (LIVE + DATED + NA windows,
    /// credits + plan), exercising the whole build/layout/position/tick path, then exit. Writes PASS/FAIL
    /// to the file named by <c>AIUSAGE_SELFTEST_OUT</c> (or the temp dir) and sets the process exit code,
    /// so a headless smoke run can assert the popup type loads without a UI to click.
    /// </summary>
    private void RunPopupSelfTestAndExit()
    {
        string outPath = Environment.GetEnvironmentVariable("AIUSAGE_SELFTEST_OUT")
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aiusage-selftest-result.txt");

        int exitCode;
        string result;
        try
        {
            var popup = new UsagePopup(_clock, onRefresh: static () => { }, onExit: static () => { });
            try
            {
                var pngPath = Environment.GetEnvironmentVariable("AIUSAGE_SELFTEST_PNG");
                popup.RunOffscreenSelfTest(BuildSampleView(_clock.GetUtcNow()), pngPath);
            }
            finally
            {
                popup.CloseForReal();
            }

            result = "PASS";
            exitCode = 0;
        }
        catch (Exception ex)
        {
            result = "FAIL: " + ex;
            exitCode = 1;
        }

        try
        {
            System.IO.File.WriteAllText(outPath, result + Environment.NewLine);
        }
        catch (Exception ioEx)
        {
            System.Diagnostics.Debug.WriteLine($"Self-test result write failed: {ioEx}");
        }

        Shutdown(exitCode);
    }

    /// <summary>
    /// Construct and lay out the <see cref="SettingsWindow"/> off-screen with the default config (no
    /// registry touch — the autostart bool is passed in), exercising the whole build/layout path, then
    /// exit. Writes PASS/FAIL to the file named by <c>AIUSAGE_SELFTEST_OUT</c> (or the temp dir) and sets
    /// the process exit code, so a headless smoke run can assert the settings window type loads without a
    /// UI to click. Never on the shipping path (guarded by <c>AIUSAGE_SELFTEST=settings</c>).
    /// </summary>
    private void RunSettingsSelfTestAndExit()
    {
        string outPath = Environment.GetEnvironmentVariable("AIUSAGE_SELFTEST_OUT")
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aiusage-settings-selftest-result.txt");

        int exitCode;
        string result;
        try
        {
            var window = new SettingsWindow(AppConfig.Default, startWithWindows: false, onApply: static _ => { });
            try
            {
                window.RunOffscreenSelfTest();
            }
            finally
            {
                window.CloseForReal();
            }

            result = "PASS";
            exitCode = 0;
        }
        catch (Exception ex)
        {
            result = "FAIL: " + ex;
            exitCode = 1;
        }

        try
        {
            System.IO.File.WriteAllText(outPath, result + Environment.NewLine);
        }
        catch (Exception ioEx)
        {
            System.Diagnostics.Debug.WriteLine($"Settings self-test result write failed: {ioEx}");
        }

        Shutdown(exitCode);
    }

    /// <summary>
    /// A representative render model for the self-test: the COMBINED popup (T31/T32). A Codex card (a LIVE
    /// weekly warning, a DATED 5h, and an NA window; credits + plan) AND a Claude card (a LIVE 5h, a LIVE
    /// Weekly, and a LIVE per-model "Fable wk" scoped window at warning; credits + plan) — so the captured
    /// PNG shows both providers exactly as they render live. Built with the public view records directly so
    /// it exercises every popup render branch (incl. the scoped-window label) without touching a data source.
    /// </summary>
    private static UsageView BuildSampleView(DateTimeOffset now)
    {
        var liveWeekly = new WindowView(
            ProviderId: "codex",
            WindowMinutes: WindowClassifier.WeeklyMinutes,
            Label: "Weekly",
            DisplayState: DisplayState.Live,
            Percent: 82.4m,
            ObservedAt: now,
            ResetsAt: Metric.Available(now.AddHours(41).AddMinutes(12), now),
            Severity: Severity.Warning,
            ReasonCode: null);

        var datedFiveHour = new WindowView(
            ProviderId: "codex",
            WindowMinutes: WindowClassifier.FiveHourMinutes,
            Label: "5h",
            DisplayState: DisplayState.Dated,
            Percent: 4.0m,
            ObservedAt: now.AddMinutes(-90),
            ResetsAt: Metric.Available(now.AddHours(2), now.AddMinutes(-90)),
            Severity: Severity.Normal,
            ReasonCode: null);

        var naWindow = new WindowView(
            ProviderId: "codex",
            WindowMinutes: 1440,
            Label: "24h",
            DisplayState: DisplayState.NA,
            Percent: null,
            ObservedAt: null,
            ResetsAt: Metric.Unavailable<DateTimeOffset>("no-recent-event"),
            Severity: Severity.Normal,
            ReasonCode: "no-recent-event");

        var codex = new ProviderView(
            ProviderId: "codex",
            FetchedAt: now.AddSeconds(-12),
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: new[] { datedFiveHour, liveWeekly, naWindow },
            CreditsBalance: Metric.Available(12.4m, now),
            PlanType: Metric.Available("Pro", now),
            Severity: Severity.Warning,
            Unknown: true,
            AllUnknown: false);

        // Claude card: three LIVE windows (5h, weekly_all, and a weekly_scoped per-model "Fable wk"),
        // matching the ClaudeUsageParser shape (kind session/weekly_all/weekly_scoped, DESIGN.md §4.2).
        var claudeFiveHour = new WindowView(
            ProviderId: "claude",
            WindowMinutes: WindowClassifier.FiveHourMinutes,
            Label: "5h",
            DisplayState: DisplayState.Live,
            Percent: 34.7m,
            ObservedAt: now.AddSeconds(-40),
            ResetsAt: Metric.Available(now.AddHours(3).AddMinutes(20), now.AddSeconds(-40)),
            Severity: Severity.Normal,
            ReasonCode: null);

        var claudeWeekly = new WindowView(
            ProviderId: "claude",
            WindowMinutes: WindowClassifier.WeeklyMinutes,
            Label: "Weekly",
            DisplayState: DisplayState.Live,
            Percent: 61.2m,
            ObservedAt: now.AddSeconds(-40),
            ResetsAt: Metric.Available(now.AddDays(4).AddHours(6), now.AddSeconds(-40)),
            Severity: Severity.Normal,
            ReasonCode: null);

        var claudeFableWeekly = new WindowView(
            ProviderId: "claude",
            WindowMinutes: WindowClassifier.WeeklyMinutes,
            Label: "Fable wk",
            DisplayState: DisplayState.Live,
            Percent: 83.9m,
            ObservedAt: now.AddSeconds(-40),
            ResetsAt: Metric.Available(now.AddDays(4).AddHours(6), now.AddSeconds(-40)),
            Severity: Severity.Warning,
            ReasonCode: null);

        var claude = new ProviderView(
            ProviderId: "claude",
            FetchedAt: now.AddSeconds(-40),
            Status: SourceStatus.Ok,
            StatusReasonCode: null,
            Windows: new[] { claudeFiveHour, claudeWeekly, claudeFableWeekly },
            CreditsBalance: Metric.Available(8.75m, now),
            PlanType: Metric.Available("Max", now),
            Severity: Severity.Warning,
            Unknown: false,
            AllUnknown: false);

        // Providers ordered by id (Ordinal): "claude" before "codex", matching UsageViewBuilder's ordering.
        return new UsageView(Severity.Warning, Unknown: true, AllUnknown: false, new[] { claude, codex });
    }
}
