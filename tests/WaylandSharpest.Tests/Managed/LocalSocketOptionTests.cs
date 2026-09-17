using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests.Managed;

public sealed class LocalSocketOptionTests
{
    [Fact]
    public void Default_follows_the_host()
    {
        var expected = !OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser();
        Assert.Equal(expected, new ManagedTransportOptions().LocalSocket);

        using var display = WlServerDisplay.Create(new ManagedTransport());
        Assert.Equal(expected, display.SupportsLocalSocket);
    }

    [Fact]
    public void Disabled_reports_false_and_refuses_to_bind()
    {
        using var display = WlServerDisplay.Create(
            new ManagedTransport(new ManagedTransportOptions { LocalSocket = false }));

        Assert.False(display.SupportsLocalSocket);
        var auto = Assert.Throws<InvalidOperationException>(() => display.AddSocketAuto());
        Assert.Contains(nameof(ManagedTransportOptions.LocalSocket), auto.Message);
        var named = Assert.Throws<InvalidOperationException>(() => display.AddSocket("wayland-test-none"));
        Assert.Contains(nameof(ManagedTransportOptions.LocalSocket), named.Message);
    }
}
