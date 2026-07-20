using Microsoft.Win32;

namespace AIUsageTray;

/// <summary>
/// A minimal seam over the HKCU <c>Run</c> registry key so the autostart logic in <see cref="Autostart"/>
/// is unit-testable with an in-memory fake — no real registry writes in tests.
/// </summary>
public interface IRunKey
{
    /// <summary>The current string value under <paramref name="name"/>, or <c>null</c> if absent.</summary>
    string? GetValue(string name);

    /// <summary>Create-or-overwrite the string value under <paramref name="name"/>.</summary>
    void SetValue(string name, string value);

    /// <summary>Remove the value under <paramref name="name"/>; a no-op if it is already absent.</summary>
    void DeleteValue(string name);
}

/// <summary>
/// The real <see cref="IRunKey"/>: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> — the
/// per-user run-at-sign-in key (no elevation, DESIGN.md §7 Windows integration 3).
/// </summary>
public sealed class HkcuRunKey : IRunKey
{
    /// <summary>The documented per-user startup key path.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>Reads/writes the "run at sign-in" state. Abstracted so callers stay off the concrete registry.</summary>
public interface IAutostart
{
    /// <summary>Whether AI-Usage is currently registered to start at sign-in.</summary>
    bool IsEnabled();

    /// <summary>Add (<c>true</c>) or remove (<c>false</c>) the sign-in autostart registration.</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// The HKCU <c>Run</c> autostart toggle (DESIGN.md §7 Windows integration 3; task T38). Presence of the
/// <see cref="ValueName"/> value means "start at sign-in"; enabling writes the current executable's
/// (quoted) path, disabling removes the value. Deliberately NON-THROWING: a registry fault (policy
/// lockdown, transient sharing) is swallowed/logged so the Settings path can never take the tray down.
/// The registry access sits behind <see cref="IRunKey"/> so the presence/quoting logic is testable
/// without touching the real registry.
/// </summary>
public sealed class Autostart : IAutostart
{
    /// <summary>The HKCU Run value name (DESIGN.md §7): <c>AIUsage</c>.</summary>
    public const string ValueName = "AIUsage";

    private readonly IRunKey _runKey;
    private readonly string _exePath;

    /// <param name="runKey">The registry Run-key seam.</param>
    /// <param name="exePath">The executable path to register when enabling (quoted defensively).</param>
    public Autostart(IRunKey runKey, string exePath)
    {
        _runKey = runKey ?? throw new ArgumentNullException(nameof(runKey));
        _exePath = exePath ?? string.Empty;
    }

    /// <summary>Bind to the real HKCU Run key and this process's own executable path.</summary>
    public static Autostart ForCurrentUser() => new(new HkcuRunKey(), Environment.ProcessPath ?? string.Empty);

    /// <inheritdoc />
    public bool IsEnabled()
    {
        try
        {
            return !string.IsNullOrEmpty(_runKey.GetValue(ValueName));
        }
        catch (Exception ex)
        {
            Log(ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (string.IsNullOrEmpty(_exePath))
                {
                    // No resolvable exe path (rare hosting edge) — nothing sensible to register; leave it off.
                    return;
                }

                _runKey.SetValue(ValueName, Quote(_exePath));
            }
            else
            {
                _runKey.DeleteValue(ValueName);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    // Quote the path so a Program Files-style path with spaces is parsed as one token by the shell.
    private static string Quote(string path) => path.StartsWith('"') ? path : $"\"{path}\"";

    private static void Log(Exception ex)
        => System.Diagnostics.Debug.WriteLine($"Autostart registry access failed: {ex}");
}
