namespace AIUsageTray;

/// <summary>
/// Process entry point. WPF requires a single-threaded-apartment thread for its message loop, so
/// <see cref="Main"/> is marked <c>[STAThread]</c>.
/// </summary>
internal static class Program
{
    // Per-session single-instance guard (DESIGN.md §7 Windows integration 2; task T21). The "Local\"
    // prefix scopes the mutex to this logon session — one tray per user session, never two icons or
    // doubled polling. The GUID keeps the name from colliding with any other app's mutex.
    private const string SingleInstanceMutexName = "Local\\AIUsageTray-SingleInstance-8F2C1A67-6C2E-4B9E-9E1B-2A9C0F4D77B1";

    /// <summary>
    /// Named auto-reset event the primary instance listens on and a second launch signals so the running tray
    /// activates its popup (DESIGN.md §7 Windows integration 2; review P2-18) rather than the second process
    /// only flashing a MessageBox. Same "Local\" session scope + GUID discipline as the single-instance mutex.
    /// </summary>
    internal const string ActivateEventName = "Local\\AIUsageTray-Activate-8F2C1A67-6C2E-4B9E-9E1B-2A9C0F4D77B1";

    [STAThread]
    private static void Main()
    {
        // Per-Monitor-V2 DPI awareness (task T14). Set here — before any HWND (the tray icon or the WPF
        // popup) exists — rather than in the manifest, because the WinForms build analyzer (WFO0003) owns
        // the manifest DPI keys in a UseWindowsForms project. This applies process-wide, so the WPF popup
        // is crisp on mixed-DPI monitors. Best-effort: a false return (already set / unsupported) is fine.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);

        // The guarded offscreen self-test (AIUSAGE_SELFTEST) constructs a window off-screen, renders it,
        // and exits immediately — it never shows a tray icon. It must therefore NOT be gated by the
        // single-instance mutex, so a dev/CI render can run while a real instance is already live.
        // App.OnStartup runs the self-test path and shuts down; we just skip acquiring the mutex here.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AIUSAGE_SELFTEST")))
        {
            Environment.ExitCode = new App().Run();
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already holds the mutex (T21: never two instances). Signal the running instance
            // to surface its popup (review P2-18); only fall back to informing the user if that channel isn't
            // up yet (e.g. the primary is still starting).
            if (!SignalExistingInstance())
            {
                System.Windows.Forms.MessageBox.Show(
                    "AI-Usage is already running.",
                    AppInfo.Name,
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }

            return;
        }

        var app = new App();
        // Propagate the WPF shutdown code (0 = normal Exit; the guarded self-test uses non-zero on failure)
        // so a smoke run can assert startup health from the process exit code.
        Environment.ExitCode = app.Run();

        // Keep the owned mutex alive for the whole app lifetime (until Run returns on Exit).
        GC.KeepAlive(mutex);
    }

    /// <summary>
    /// Open the primary instance's activation event and signal it so the running tray shows its popup
    /// (review P2-18). Returns false if the event can't be reached, so the caller can fall back to a message.
    /// Best-effort — never throws.
    /// </summary>
    private static bool SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Signal existing instance failed: {ex}");
        }

        return false;
    }
}
