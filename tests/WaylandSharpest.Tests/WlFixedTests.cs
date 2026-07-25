using Wayland;
using Xunit;

namespace WaylandSharpest.Tests;

public class WlFixedTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(255.5)]
    [InlineData(-1234.25)]
    public void Double_roundtrips(double value)
    {
        Assert.Equal(value, WlFixed.FromDouble(value).ToDouble());
    }

    [Fact]
    public void Int_roundtrips()
    {
        Assert.Equal(42, WlFixed.FromInt(42).ToInt());
        Assert.Equal(-7, WlFixed.FromInt(-7).ToInt());
    }

    [Fact]
    public void Raw_matches_wire_format()
    {
        // 24.8 fixed point: 1.0 == 256.
        Assert.Equal(256, WlFixed.FromInt(1).Raw);
        Assert.Equal(128, WlFixed.FromDouble(0.5).Raw);
    }
}

public class InterfaceSpecTests
{
    [Fact]
    public void Native_interface_graph_builds()
    {
        // wl_registry.bind ("usun") and cyclic references (wl_data_device <->
        // wl_data_offer) must materialize without recursion issues.
        Assert.NotEqual(0, (nint)WlRegistry.Interface.NativeHandle);
        Assert.NotEqual(0, (nint)WlDataDevice.Interface.NativeHandle);
        Assert.Equal("wl_registry", WlRegistry.Interface.Name);
        Assert.Equal(4, WlRegistry.Interface.Requests[0].Signature.Length);
    }
}
