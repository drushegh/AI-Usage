using System.Runtime.InteropServices;

namespace ClaudeUsageProbe;

/// <summary>
/// Best-effort steps to keep a probe crash from ever persisting the in-memory token (DESIGN §2/§6).
/// All steps are wrapped so they can never make the probe fail — hardening only.
/// <para>
/// The token lives only in this process's memory and is erased by the process exiting. The remaining
/// risk is a crash dump capturing that memory. Defence in layers:
/// </para>
/// <list type="number">
///   <item><b>Never crash.</b> The PRIMARY guarantee: <see cref="Program"/> wraps <c>Main</c> so every
///   exception is caught and turned into an exit code — the process always exits normally, never via an
///   unhandled-exception crash. Crash-dump collection (WER and the .NET minidump writer) triggers on a
///   crash, not on a normal <c>return</c>, so that path is never reached, and the runtime's default crash
///   printer never writes exception text to stderr.</item>
///   <item><b>No dump-enabling switches.</b> The .NET runtime writes a minidump ONLY when the
///   environment sets <c>DOTNET_DbgEnableMiniDump=1</c> (or legacy <c>COMPlus_DbgEnableMiniDump</c>).
///   The probe never sets it and the tray launcher must not either; there is no such switch in the
///   csproj.</item>
///   <item><b>Runtime WER UI suppression.</b> <see cref="SetErrorMode"/> disables the WER "app crashed"
///   dialog and the critical-error / GPF box for this process, so even a fatal runtime error (e.g. an
///   access violation that bypasses managed <c>catch</c>) produces no interactive WER report.</item>
///   <item><b>Deployment step (machine policy, cannot be done in-process without admin):</b> ensure no
///   <c>HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\ClaudeUsageProbe.exe</c> key
///   enabling LocalDumps exists; the installer must not create one (documented for the install script,
///   PLAN T42). WER LocalDumps is the only crash-dump vector this process cannot fully close itself.</item>
/// </list>
/// </summary>
internal static class CrashHardening
{
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    public static void HardenBestEffort()
    {
        try
        {
            _ = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        }
        catch
        {
            // Non-fatal: hardening only.
        }
    }

    // Classic DllImport (not LibraryImport), matching AIUsageTray.NativeMethods: LibraryImport's
    // source-generated marshalling requires <AllowUnsafeBlocks>, which this app avoids (DESIGN §6).
    // A single blittable call.
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = false)]
    private static extern uint SetErrorMode(uint uMode);
}
