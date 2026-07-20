using System.Runtime.InteropServices;

namespace AIUsageTray;

/// <summary>P/Invoke declarations.</summary>
internal static class NativeMethods
{
    // Classic DllImport (not LibraryImport) is used deliberately: LibraryImport's
    // source-generated marshalling requires <AllowUnsafeBlocks>, and this app avoids
    // enabling unsafe code (DESIGN.md §6 posture). This is a single blittable call.

    /// <summary>
    /// Destroys a GDI icon handle. <see cref="System.Drawing.Bitmap.GetHicon"/> allocates
    /// an HICON the caller owns; it must be freed with this to avoid a handle leak.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint handle);
}
