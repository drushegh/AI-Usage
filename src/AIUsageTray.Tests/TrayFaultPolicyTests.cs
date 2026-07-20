using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Covers the exception-severity classifier the tray's global backstop uses (review P1-4). The contract is
/// asymmetric on purpose: a monitoring tray must SURVIVE transient render/timing faults (they are non-fatal,
/// handled, and the icon degrades honestly), and only bail on genuinely corrupted process state.
/// </summary>
public sealed class TrayFaultPolicyTests
{
    [Fact]
    public void RenderClassFaults_AreNonFatal()
    {
        // The faults a lock-screen / RDP / handle-pressure GDI transition actually throws.
        Assert.False(TrayFaultPolicy.IsFatal(new InvalidOperationException()));
        Assert.False(TrayFaultPolicy.IsFatal(new ExternalException()));       // GDI+ status
        Assert.False(TrayFaultPolicy.IsFatal(new OutOfMemoryException()));    // GDI+ surfaces this for non-memory faults
        Assert.False(TrayFaultPolicy.IsFatal(new ArgumentException()));
        Assert.False(TrayFaultPolicy.IsFatal(new TimeoutException()));
    }

    [Fact]
    public void CorruptedStateFaults_AreFatal()
    {
        Assert.True(TrayFaultPolicy.IsFatal(new StackOverflowException()));
        Assert.True(TrayFaultPolicy.IsFatal(new AccessViolationException()));
        Assert.True(TrayFaultPolicy.IsFatal(new SEHException()));
    }

    [Fact]
    public void WrappedFault_IsClassifiedByItsInner()
    {
        // A single-inner AggregateException / TargetInvocationException is peeled so the real fault decides.
        Assert.True(TrayFaultPolicy.IsFatal(new AggregateException(new AccessViolationException())));
        Assert.False(TrayFaultPolicy.IsFatal(new AggregateException(new InvalidOperationException())));
        Assert.True(TrayFaultPolicy.IsFatal(
            new TargetInvocationException(new StackOverflowException())));
        Assert.False(TrayFaultPolicy.IsFatal(
            new TargetInvocationException(new ExternalException())));
    }

    [Fact]
    public void MultiInnerAggregate_IsNotPeeled_AndTreatedNonFatal()
    {
        // An aggregate of several faults is not unambiguously one corrupted-state fault — stay alive.
        var aggregate = new AggregateException(new AccessViolationException(), new InvalidOperationException());
        Assert.False(TrayFaultPolicy.IsFatal(aggregate));
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TrayFaultPolicy.IsFatal(null!));
    }
}
