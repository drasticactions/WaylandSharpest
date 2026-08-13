using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Tests for the registry name a global is advertised under, which protocols
/// like river's and ext-transient-seat require a server to tell a client.
/// </summary>
public class GlobalNameTests : LoopbackHarness
{
    /// <summary>Runs against libwayland.</summary>
    public GlobalNameTests()
    {
    }

    /// <summary>Runs against the transport a twin supplies.</summary>
    protected GlobalNameTests(global::Wayland.Server.IWlServerTransport transport) : base(transport)
    {
    }

    /// <summary>
    /// True when the loaded libwayland-server is new enough for
    /// <c>wl_global_get_name</c> (1.22). On an older library every call throws
    /// rather than returning a name that would be wrong on the wire.
    /// </summary>
    private static bool CanReadNames(WlGlobal global, WlClient client)
    {
        try
        {
            global.NameFor(client);
            return true;
        }
        catch (WaylandException ex) when (ex.Message.Contains("wl_global_get_name"))
        {
            return false;
        }
    }

    [Fact]
    public void Global_name_matches_registry_announcement()
    {
        using var first = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });
        using var middle = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });
        using var last = Server.CreateGlobal(WlOutput.Interface, 4, static (_, _, _) => { });

        if (!CanReadNames(middle, ServerClient))
        {
            Assert.Skip("libwayland-server is older than 1.22 and does not export wl_global_get_name.");
        }

        using var registry = Client.GetRegistry();
        var announced = new Dictionary<string, uint>();
        registry.Global += (_, e) => announced[e.Interface] = e.Name;
        PumpToClient();

        Assert.Equal(3, announced.Count);
        Assert.Equal(announced["wl_seat"], middle.NameFor(ServerClient));
        Assert.Equal(announced["wl_compositor"], first.NameFor(ServerClient));
        Assert.Equal(announced["wl_output"], last.NameFor(ServerClient));
    }

    [Fact]
    public void Global_name_is_zero_for_filtered_client()
    {
        using var visible = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });
        using var hidden = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        if (!CanReadNames(visible, ServerClient))
        {
            Assert.Skip("libwayland-server is older than 1.22 and does not export wl_global_get_name.");
        }

        Server.SetGlobalFilter((_, _, interfaceName) => interfaceName != "wl_seat");

        using var registry = Client.GetRegistry();
        var announced = new List<string>();
        registry.Global += (_, e) => announced.Add(e.Interface);
        PumpToClient();

        Assert.DoesNotContain("wl_seat", announced);
        Assert.Equal(0u, hidden.NameFor(ServerClient));
        Assert.NotEqual(0u, visible.NameFor(ServerClient));
    }

    [Fact]
    public void Global_reports_its_published_version()
    {
        using var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        Assert.Equal(5u, global.Version);
    }

    [Fact]
    public void Disposed_global_rejects_name_lookup()
    {
        var global = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });
        global.Dispose();

        Assert.Throws<ObjectDisposedException>(() => global.NameFor(ServerClient));
    }
}

/// <summary>The same tests, against the managed transport.</summary>
[Trait("Transport", "Managed")]
public sealed class GlobalNameTestsManaged() : GlobalNameTests(new global::Wayland.Server.ManagedTransport());
