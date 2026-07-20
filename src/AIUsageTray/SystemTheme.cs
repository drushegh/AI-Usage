using Microsoft.Win32;

namespace AIUsageTray;

/// <summary>
/// Reads the taskbar light/dark preference so the tray icon's contrast outline can adapt
/// (DESIGN.md §7 theme-awareness — a nice-to-have, never load-bearing). Uses the documented
/// <c>SystemUsesLightTheme</c> value (the taskbar/system chrome preference — distinct from
/// <c>AppsUseLightTheme</c>, which governs app windows).
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// True when the taskbar is light. Best-effort: any read failure (missing key, policy lockdown)
    /// falls back to <c>false</c> — the Windows default dark taskbar — and never throws into the
    /// render path.
    /// </summary>
    public static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("SystemUsesLightTheme") is int value)
            {
                return value != 0;
            }
        }
        catch (Exception)
        {
            // Theme detection is cosmetic; a failed registry read must never break icon rendering.
        }

        return false;
    }

    /// <summary>
    /// True when app windows use the LIGHT theme (the documented <c>AppsUseLightTheme</c> preference —
    /// distinct from <c>SystemUsesLightTheme</c>, which governs the taskbar/system chrome). Used to pick
    /// the popup's light/dark palette. Best-effort: any read failure falls back to <c>false</c> (the
    /// Windows default dark app theme) and never throws into the render path.
    /// </summary>
    public static bool IsLightAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value != 0;
            }
        }
        catch (Exception)
        {
            // Cosmetic only — a failed read must never break the popup.
        }

        return false;
    }
}
