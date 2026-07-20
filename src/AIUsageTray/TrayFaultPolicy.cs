using System.Reflection;
using System.Runtime.InteropServices;

namespace AIUsageTray;

/// <summary>
/// Classifies a caught exception as <b>fatal</b> (a corrupted-process-state fault where continuing is
/// unsafe) or <b>non-fatal</b> (a transient render/timing fault a monitoring tray must survive). This is the
/// single policy the WPF <see cref="App"/> exception backstop uses to decide whether to mark a
/// <c>DispatcherUnhandledException</c> handled: a silently-absent monitor is the worst failure mode
/// (DESIGN.md §7), so anything recoverable is swallowed and the icon degrades honestly rather than the
/// process vanishing. Pure and side-effect-free so it is unit-testable without a UI.
/// </summary>
/// <remarks>
/// GDI+ is a notorious source of "spurious" exception types: it surfaces resource/parameter faults as
/// <see cref="OutOfMemoryException"/> and <see cref="ExternalException"/> rather than actual memory
/// exhaustion, and a lock-screen / RDP / handle-pressure transition can throw
/// <see cref="InvalidOperationException"/> from <c>Graphics.FromImage</c>/<c>GetHicon</c>. None of those mean
/// the process is unrecoverable, so they are treated as non-fatal. Only genuinely corrupted-state
/// exceptions (stack overflow, access violation, structured-exception faults) are fatal — and those are not
/// even delivered to managed handlers on modern .NET, so classifying them is purely defensive.
/// </remarks>
public static class TrayFaultPolicy
{
    /// <summary>
    /// True only for corrupted-process-state exceptions where continuing is unsafe. Everything else — GDI
    /// <see cref="ExternalException"/>, <see cref="InvalidOperationException"/>,
    /// <see cref="OutOfMemoryException"/> raised while rendering, etc. — is non-fatal.
    /// </summary>
    public static bool IsFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return Unwrap(exception) is StackOverflowException
            or AccessViolationException
            or SEHException;
    }

    /// <summary>Peel single-inner wrapper exceptions so the underlying fault drives the decision.</summary>
    private static Exception Unwrap(Exception exception) => exception switch
    {
        AggregateException { InnerExceptions.Count: 1 } agg => Unwrap(agg.InnerExceptions[0]),
        TargetInvocationException { InnerException: { } inner } => Unwrap(inner),
        _ => exception,
    };
}
