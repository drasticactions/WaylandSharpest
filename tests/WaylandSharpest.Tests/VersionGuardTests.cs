using Wayland;
using WaylandSharpest.Tests.Protocol;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Neither side of the wire checks the negotiated version before marshalling,
/// so a too-new message is a protocol violation libwayland does not catch: the
/// peer fails to decode and the connection dies pointing nowhere near the
/// cause. The generated guards make it a local throw instead.
/// </summary>
public sealed class VersionGuardTests : LoopbackHarness
{
    private TestFactoryResource? _serverFactory;

    private TestFactory BindAt(uint version)
    {
        Server.CreateGlobal(TestFactory.Interface, 2, (client, boundVersion, id) =>
            _serverFactory = new TestFactoryResource(client, boundVersion, id));
        var proxy = Bind<TestFactory>("test_factory", version);
        PumpToServer();
        Assert.NotNull(_serverFactory);
        return proxy;
    }

    [Fact]
    public void Sending_a_too_new_event_throws_instead_of_corrupting_the_wire()
    {
        using var proxy = BindAt(1);
        Assert.Equal(1u, _serverFactory!.Version);
        Assert.False(_serverFactory.SupportsSendReadyV2);

        var ex = Assert.Throws<WaylandVersionException>(() => _serverFactory.SendReadyV2());
        Assert.Equal("test_factory", ex.InterfaceName);
        Assert.Equal("ready_v2", ex.MessageName);
        Assert.Equal(2u, ex.RequiredVersion);
        Assert.Equal(1u, ex.ActualVersion);

        // The connection is untouched: an event the client can decode still arrives.
        var ready = 0;
        proxy.Ready += (_, _) => ready++;
        _serverFactory.SendReady();
        PumpEventsToClient();

        Assert.Equal(1, ready);
        Assert.False(proxy.IsDestroyed);
    }

    [Fact]
    public void Sending_a_supported_event_passes_the_guard()
    {
        using var proxy = BindAt(2);
        Assert.True(_serverFactory!.SupportsSendReadyV2);

        var received = 0;
        proxy.ReadyV2 += (_, _) => received++;
        _serverFactory.SendReadyV2();
        PumpEventsToClient();

        Assert.Equal(1, received);
    }

    [Fact]
    public void Calling_a_too_new_request_throws_on_the_client_side()
    {
        using var proxy = BindAt(1);
        Assert.False(proxy.SupportsPokeV2);

        var ex = Assert.Throws<WaylandVersionException>(() => proxy.PokeV2());
        Assert.Equal("test_factory", ex.InterfaceName);
        Assert.Equal("poke_v2", ex.MessageName);
        Assert.Equal(2u, ex.RequiredVersion);
        Assert.Equal(1u, ex.ActualVersion);
    }

    [Fact]
    public void Calling_a_supported_request_passes_the_guard()
    {
        using var proxy = BindAt(2);
        Assert.True(proxy.SupportsPokeV2);

        var poked = 0;
        _serverFactory!.PokeV2 += (_, _) => poked++;
        proxy.PokeV2();
        PumpToServer();

        Assert.Equal(1, poked);
    }
}
