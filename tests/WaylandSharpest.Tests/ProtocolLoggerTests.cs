using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// The structured form of <c>WAYLAND_DEBUG=1</c>: a callback per message with
/// direction, object, opcode and decoded arguments.
/// </summary>
public class ProtocolLoggerTests : LoopbackHarness
{
    /// <summary>Runs against libwayland.</summary>
    public ProtocolLoggerTests()
    {
    }

    /// <summary>Runs against the transport a twin supplies.</summary>
    protected ProtocolLoggerTests(global::Wayland.Server.IWlServerTransport transport) : base(transport)
    {
    }

    private readonly record struct Entry(
        WlProtocolMessageDirection Direction,
        string Interface,
        string Message,
        int Opcode,
        uint ResourceId,
        string Signature,
        string Rendered);

    /// <summary>
    /// Copies out of the ref struct inside the callback — the argument storage
    /// belongs to libwayland and is gone by the time the test looks.
    /// </summary>
    private static List<Entry> Capture(WlServerDisplay server, out IDisposable registration)
    {
        var log = new List<Entry>();
        registration = server.AddProtocolLogger((in WlProtocolMessage m) => log.Add(new Entry(
            m.Direction, m.InterfaceName, m.MessageName, m.Opcode, m.ResourceId, m.Signature, m.ToString())));
        return log;
    }

    [Fact]
    public void Protocol_logger_sees_both_directions()
    {
        var log = Capture(Server, out var registration);
        using (registration)
        {
            using var global = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });
            using var compositor = Bind<WlCompositor>("wl_compositor", 6);
            PumpToServer();

            var bind = Assert.Single(log, e => e.Message == "bind");
            Assert.Equal(WlProtocolMessageDirection.Request, bind.Direction);
            Assert.Equal("wl_registry", bind.Interface);
            Assert.Equal(0, bind.Opcode);

            var announce = Assert.Single(log, e => e.Message == "global" && e.Interface == "wl_registry");
            Assert.Equal(WlProtocolMessageDirection.Event, announce.Direction);
            Assert.Equal(0, announce.Opcode);
            Assert.Equal("usu", announce.Signature);
            Assert.Contains("wl_compositor", announce.Rendered);
        }
    }

    [Fact]
    public void Disposing_the_registration_stops_logging()
    {
        var log = Capture(Server, out var registration);
        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });
        using var registry = Client.GetRegistry();
        PumpToClient();

        Assert.NotEmpty(log);
        registration.Dispose();
        var seen = log.Count;

        using var compositor = Bind<WlCompositor>("wl_compositor", 6);
        PumpToServer();

        Assert.Equal(seen, log.Count);
    }

    [Fact]
    public void Disposing_the_registration_twice_is_harmless()
    {
        var _ = Capture(Server, out var registration);
        registration.Dispose();
        registration.Dispose();
    }

    [Fact]
    public void Logged_requests_report_the_resource_they_arrived_on()
    {
        var log = Capture(Server, out var registration);
        using (registration)
        {
            WlCompositorResource? bound = null;
            using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
                bound = new WlCompositorResource(client, version, id));

            using var compositor = Bind<WlCompositor>("wl_compositor", 6);
            using var surface = compositor.CreateSurface();
            PumpToServer();

            Assert.NotNull(bound);
            var create = Assert.Single(log, e => e.Message == "create_surface");
            Assert.Equal("wl_compositor", create.Interface);
            Assert.Equal(bound!.Id, create.ResourceId);
            Assert.Contains("new id", create.Rendered);
        }
    }

    [Fact]
    public void A_throwing_logger_surfaces_on_dispatch_rather_than_unwinding()
    {
        using var registration = Server.AddProtocolLogger(
            static (in WlProtocolMessage _) => throw new InvalidOperationException("log boom"));

        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });
        using var registry = Client.GetRegistry();
        Client.Flush();

        var ex = Assert.Throws<WaylandException>(() => Server.EventLoop.Dispatch(100));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}

/// <summary>The same tests, against the managed transport.</summary>
[Trait("Transport", "Managed")]
public sealed class ProtocolLoggerTestsManaged() : ProtocolLoggerTests(new global::Wayland.Server.ManagedTransport());
