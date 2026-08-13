using Wayland;
using Wayland.Server;
using WaylandSharpest.Tests.Protocol;
using Xunit;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// A client that finds the server the ordinary way: by name, under the runtime
/// directory, with the lock file that keeps two servers from claiming it.
/// </summary>
public sealed class ManagedListeningSocketTests
{
    private static void SkipWithoutRuntimeDirectory()
    {
        TestHost.SkipWithoutFdPassingSockets();

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")))
        {
            Assert.Skip("XDG_RUNTIME_DIR is not set, so there is nowhere to bind.");
        }
    }

    private static string UniqueName() => $"waylandsharpest-test-{Guid.NewGuid():N}"[..30];

    [Fact]
    public void A_client_connects_by_name_and_sees_the_globals()
    {
        SkipWithoutRuntimeDirectory();
        TestHost.SkipWithoutLibwayland();

        using var server = WlServerDisplay.Create(new ManagedTransport());
        var name = UniqueName();
        server.AddSocket(name);

        using var global = server.CreateGlobal(TestFactory.Interface, 2, (client, version, id) =>
            _ = new TestFactoryResource(client, version, id));

        using var client = WlDisplay.Connect(name);
        using var registry = client.GetRegistry();

        var advertised = new List<string>();
        registry.Global += (_, e) => advertised.Add(e.Interface);

        client.Flush();
        server.EventLoop.Dispatch(500);
        server.EventLoop.Dispatch(200);
        server.FlushClients();
        client.Dispatch();

        Assert.Contains("test_factory", advertised);
        Assert.Single(server.Clients);
    }

    [Fact]
    public void A_second_server_cannot_take_a_name_that_is_held()
    {
        SkipWithoutRuntimeDirectory();

        using var first = WlServerDisplay.Create(new ManagedTransport());
        var name = UniqueName();
        first.AddSocket(name);

        using var second = WlServerDisplay.Create(new ManagedTransport());
        var refused = Assert.Throws<WaylandException>(() => second.AddSocket(name));
        Assert.Contains("already holds", refused.Message);
    }

    [Fact]
    public void A_name_becomes_free_again_when_its_server_goes()
    {
        SkipWithoutRuntimeDirectory();

        var name = UniqueName();
        using (var first = WlServerDisplay.Create(new ManagedTransport()))
        {
            first.AddSocket(name);
        }

        using var second = WlServerDisplay.Create(new ManagedTransport());
        second.AddSocket(name);

        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!;
        Assert.True(File.Exists(Path.Combine(directory, name)));
    }

    [Fact]
    public void An_automatic_name_is_chosen_and_reported()
    {
        SkipWithoutRuntimeDirectory();

        using var server = WlServerDisplay.Create(new ManagedTransport());
        var name = server.AddSocketAuto();

        Assert.StartsWith("wayland-", name);
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!;
        Assert.True(File.Exists(Path.Combine(directory, name)));
    }

    [Fact]
    public void The_socket_and_its_lock_are_cleared_away()
    {
        SkipWithoutRuntimeDirectory();

        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!;
        var name = UniqueName();
        var path = Path.Combine(directory, name);

        using (var server = WlServerDisplay.Create(new ManagedTransport()))
        {
            server.AddSocket(name);
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".lock"));
    }
}
