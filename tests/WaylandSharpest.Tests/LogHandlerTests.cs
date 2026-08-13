using System.Runtime.InteropServices;
using Wayland;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// libwayland's own diagnostics go to stderr through a hook whose second
/// parameter is a <c>va_list</c>; these check the formatting path that makes it
/// reachable from .NET.
/// </summary>
[Collection("libwayland log handler")]
public class LogHandlerTests : LoopbackHarness
{
    /// <summary>Runs against libwayland.</summary>
    public LogHandlerTests()
    {
    }

    /// <summary>Runs against the transport a twin supplies.</summary>
    protected LogHandlerTests(global::Wayland.Server.IWlServerTransport transport) : base(transport)
    {
    }

    public override void Dispose()
    {
        WaylandLog.SetHandler(WaylandLogSide.Client, null);
        WaylandLog.SetHandler(WaylandLogSide.Server, null);
        base.Dispose();
    }

    [Fact]
    public void Unsupported_architectures_refuse_rather_than_guess()
    {
        if (WaylandLog.IsSupported)
        {
            Assert.True(RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64);
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() =>
            WaylandLog.SetHandler(WaylandLogSide.Client, static _ => { }));
    }

    [Fact]
    public void Log_handler_captures_a_protocol_error()
    {
        if (!WaylandLog.IsSupported)
        {
            Assert.Skip($"libwayland log routing is not supported on {RuntimeInformation.ProcessArchitecture}.");
        }

        var lines = new List<string>();
        WaylandLog.SetHandler(WaylandLogSide.Client, lines.Add);

        // Bind a global at a version the server never published: the server kills
        // the connection and libwayland-client logs the error it decoded.
        using var global = Server.CreateGlobal(WlCompositor.Interface, 1, static (_, _, _) => { });
        using var registry = Client.GetRegistry();
        uint name = 0;
        registry.Global += (_, e) => name = e.Name;
        PumpToClient();
        Assert.NotEqual(0u, name);

        registry.Bind<WlCompositor>(name, 6);
        PumpToServer();
        Server.FlushClients();
        Assert.ThrowsAny<WaylandException>(() => Client.Dispatch());

        // The handler is process wide, so a protocol error another test provoked
        // while it was installed lands here too. Which line arrived is not the
        // point; that one about this error did, and that it is formatted, is.
        var message = lines.FirstOrDefault(line => line.Contains("wl_registry"));
        Assert.NotNull(message);

        // The format string carries %s/%d conversions; seeing them substituted
        // is the whole point of the vsnprintf path.
        Assert.DoesNotContain("%s", message);
        Assert.DoesNotContain("%d", message);
        Assert.Contains("wl_registry", message);
    }

    [Fact]
    public void Clearing_the_handler_restores_the_default()
    {
        if (!WaylandLog.IsSupported)
        {
            Assert.Skip($"libwayland log routing is not supported on {RuntimeInformation.ProcessArchitecture}.");
        }

        var lines = new List<string>();
        WaylandLog.SetHandler(WaylandLogSide.Client, lines.Add);
        WaylandLog.SetHandler(WaylandLogSide.Client, null);

        using var global = Server.CreateGlobal(WlCompositor.Interface, 1, static (_, _, _) => { });
        using var registry = Client.GetRegistry();
        uint name = 0;
        registry.Global += (_, e) => name = e.Name;
        PumpToClient();

        registry.Bind<WlCompositor>(name, 6);
        PumpToServer();
        Server.FlushClients();
        Assert.ThrowsAny<WaylandException>(() => Client.Dispatch());

        Assert.Empty(lines);
    }

    [Fact]
    public void A_throwing_handler_does_not_unwind_into_libwayland()
    {
        if (!WaylandLog.IsSupported)
        {
            Assert.Skip($"libwayland log routing is not supported on {RuntimeInformation.ProcessArchitecture}.");
        }

        WaylandLog.SetHandler(WaylandLogSide.Client, static _ => throw new InvalidOperationException("log boom"));

        using var global = Server.CreateGlobal(WlCompositor.Interface, 1, static (_, _, _) => { });
        using var registry = Client.GetRegistry();
        uint name = 0;
        registry.Global += (_, e) => name = e.Name;
        PumpToClient();

        registry.Bind<WlCompositor>(name, 6);
        PumpToServer();
        Server.FlushClients();

        // The connection error still surfaces; the handler's exception is swallowed.
        Assert.ThrowsAny<WaylandException>(() => Client.Dispatch());
    }
}

/// <summary>
/// The same tests, against the managed transport. The log handler is process
/// wide, so this shares the collection that keeps the two from running at once.
/// </summary>
[Trait("Transport", "Managed")]
[Collection("libwayland log handler")]
public sealed class LogHandlerTestsManaged() : LogHandlerTests(new global::Wayland.Server.ManagedTransport());
