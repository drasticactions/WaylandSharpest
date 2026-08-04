using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Tests for the peer identity a global filter needs in order to decide
/// anything beyond "which interface is this".
/// </summary>
public sealed class ClientIdentityTests : LoopbackHarness
{
    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc")]
    private static extern uint getegid();

    [Fact]
    public void Client_credentials_report_this_process()
    {
        // Both peers of the socketpair are this process.
        var credentials = ServerClient.Credentials;

        Assert.Equal(Environment.ProcessId, credentials.Pid);
        Assert.Equal(geteuid(), credentials.Uid);
        Assert.Equal(getegid(), credentials.Gid);
    }

    [Fact]
    public void Client_exposes_its_connection_fd()
    {
        Assert.True(ServerClient.Fd >= 0);
    }

    [Fact]
    public void Client_get_object_returns_owned_resource()
    {
        WlCompositorResource? bound = null;
        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
            bound = new WlCompositorResource(client, version, id));

        using var compositor = Bind<WlCompositor>("wl_compositor", 6);
        PumpToServer();

        Assert.NotNull(bound);
        var id = bound!.Id;
        Assert.Same(bound, ServerClient.GetObject(id));
        Assert.Equal(bound.RawHandle, ServerClient.GetObjectHandle(id));
    }

    [Fact]
    public void Client_get_object_returns_null_for_an_unused_id()
    {
        Assert.Null(ServerClient.GetObject(0xdead));
        Assert.Equal(0, ServerClient.GetObjectHandle(0xdead));
    }

    [Fact]
    public void Global_filter_can_decide_on_credentials()
    {
        using var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });
        Server.SetGlobalFilter((client, _, _) => client.Credentials.Uid == geteuid());

        using var registry = Client.GetRegistry();
        var announced = new List<string>();
        registry.Global += (_, e) => announced.Add(e.Interface);
        PumpToClient();

        Assert.Contains("wl_seat", announced);
    }
}
