using Xunit;

namespace AIUsageTray.Tests;

public sealed class AppInfoTests
{
    [Fact]
    public void Name_IsAiUsage()
    {
        // Trivial placeholder assertion: proves the build+test CI shape and that the
        // AIUsageTray assembly loads at runtime under the test host.
        Assert.Equal("AI-Usage", AIUsageTray.AppInfo.Name);
    }
}
