using Xunit;

namespace AIUsageTray.Tests;

/// <summary>
/// Covers the HKCU Run autostart toggle (T38) through its injectable <see cref="IRunKey"/> seam — no real
/// registry writes. Asserts the presence-means-enabled logic, path quoting, remove-on-disable, the empty
/// exe-path guard, and the non-throwing contract when the registry access itself faults.
/// </summary>
public sealed class AutostartTests
{
    private const string ExePath = @"C:\Program Files\AI Usage\AIUsageTray.exe";

    [Fact]
    public void IsEnabled_NoValue_False()
    {
        var autostart = new Autostart(new FakeRunKey(), ExePath);
        Assert.False(autostart.IsEnabled());
    }

    [Fact]
    public void SetEnabledTrue_WritesQuotedExePath_AndReadsBackEnabled()
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, ExePath);

        autostart.SetEnabled(true);

        Assert.True(autostart.IsEnabled());
        // The value name is the documented "AIUsage" and the path is quoted (spaces-safe).
        Assert.Equal($"\"{ExePath}\"", key.Values[Autostart.ValueName]);
    }

    [Fact]
    public void SetEnabledFalse_RemovesValue()
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, ExePath);

        autostart.SetEnabled(true);
        autostart.SetEnabled(false);

        Assert.False(autostart.IsEnabled());
        Assert.False(key.Values.ContainsKey(Autostart.ValueName));
    }

    [Fact]
    public void SetEnabledTrue_EmptyExePath_DoesNotWrite()
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, string.Empty);

        autostart.SetEnabled(true); // nothing sensible to register

        Assert.False(autostart.IsEnabled());
        Assert.Empty(key.Values);
    }

    [Fact]
    public void SetEnabledTrue_AlreadyQuotedPath_NotDoubleQuoted()
    {
        var key = new FakeRunKey();
        var quoted = $"\"{ExePath}\"";
        var autostart = new Autostart(key, quoted);

        autostart.SetEnabled(true);

        Assert.Equal(quoted, key.Values[Autostart.ValueName]);
    }

    [Fact]
    public void RegistryFault_IsSwallowed_IsEnabledFalse_SetEnabledDoesNotThrow()
    {
        var autostart = new Autostart(new ThrowingRunKey(), ExePath);

        Assert.False(autostart.IsEnabled());          // read fault → false, no throw
        autostart.SetEnabled(true);                    // write fault → swallowed
        autostart.SetEnabled(false);                   // delete fault → swallowed
    }

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string? GetValue(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public void SetValue(string name, string value) => Values[name] = value;

        public void DeleteValue(string name) => Values.Remove(name);
    }

    private sealed class ThrowingRunKey : IRunKey
    {
        public string? GetValue(string name) => throw new InvalidOperationException("registry unavailable");

        public void SetValue(string name, string value) => throw new InvalidOperationException("registry unavailable");

        public void DeleteValue(string name) => throw new InvalidOperationException("registry unavailable");
    }
}
