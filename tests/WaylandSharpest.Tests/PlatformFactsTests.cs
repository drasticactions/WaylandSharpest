using Wayland;
using Xunit;

namespace WaylandSharpest.Tests;

public sealed class PlatformFactsTests
{
    [Fact]
    public void Kernel_facts_match_the_runtime()
    {
        Assert.Equal(
            OperatingSystem.IsLinux() || OperatingSystem.IsAndroid(),
            PlatformFacts.IsLinuxKernel);
        Assert.Equal(
            OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ||
            OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS(),
            PlatformFacts.IsApple);
        Assert.False(PlatformFacts.IsLinuxKernel && PlatformFacts.IsApple);
    }
}
