using Wayland;
using Wayland.Server;
using WaylandSharpest.Tests.Protocol;
using Xunit;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// A real libwayland client talking to a managed server over a socket. This is
/// the only test that proves the two agree about the wire, which is the point
/// of the whole transport: clients are written against libwayland, not against
/// this.
/// </summary>
public sealed class ManagedSocketTests : IDisposable
{
    private readonly WlServerDisplay _server;
    private readonly WlClient _serverClient;
    private readonly WlDisplay _client;

    public ManagedSocketTests()
    {
        TestHost.SkipWithoutFdPassingSockets();
        TestHost.SkipWithoutLibwayland();

        _server = WlServerDisplay.Create(new ManagedTransport());

        var (transport, clientFd) = WlClientTransport.CreatePair();
        _serverClient = _server.CreateClient(transport);
        _client = WlDisplay.ConnectToFd(clientFd);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server?.Dispose();
    }

    private void PumpToClient()
    {
        _client.Flush();
        _server.EventLoop.Dispatch(200);
        _server.FlushClients();
        _client.Dispatch();
    }

    private void PumpToServer()
    {
        _client.Flush();
        _server.EventLoop.Dispatch(200);
    }

    private T Bind<T>(string interfaceName, uint version)
        where T : WlProxy, IWaylandObject<T>
    {
        using var registry = _client.GetRegistry();
        uint name = 0;
        registry.Global += (_, e) =>
        {
            if (e.Interface == interfaceName)
            {
                name = e.Name;
            }
        };

        PumpToClient();
        Assert.NotEqual(0u, name);
        return registry.Bind<T>(name, version);
    }

    [Fact]
    public void A_libwayland_client_sees_the_globals_a_managed_server_publishes()
    {
        using var global = _server.CreateGlobal(TestFactory.Interface, 2, (client, version, id) =>
            _ = new TestFactoryResource(client, version, id));

        using var registry = _client.GetRegistry();
        var advertised = new List<(uint Name, string Interface, uint Version)>();
        registry.Global += (_, e) => advertised.Add((e.Name, e.Interface, e.Version));

        PumpToClient();

        var entry = Assert.Single(advertised);
        Assert.Equal("test_factory", entry.Interface);
        Assert.Equal(2u, entry.Version);
    }

    [Fact]
    public void A_request_crosses_the_socket_and_reaches_the_resource()
    {
        uint poked = 0;
        using var global = _server.CreateGlobal(TestFactory.Interface, 2, (client, version, id) =>
        {
            var factory = new TestFactoryResource(client, version, id);
            factory.MakeChild += (_, e) =>
            {
                var child = new TestChildResource(client, version, e.Id);
                child.Poke += (_, p) => poked = p.Value;
            };
        });

        using var proxy = Bind<TestFactory>("test_factory", 2);
        using var childProxy = proxy.MakeChild();
        childProxy.Poke(4242);

        PumpToServer();

        Assert.Equal(4242u, poked);
    }

    [Fact]
    public void An_event_crosses_the_socket_and_reaches_the_proxy()
    {
        TestChildResource? child = null;
        using var global = _server.CreateGlobal(TestFactory.Interface, 2, (client, version, id) =>
        {
            var factory = new TestFactoryResource(client, version, id);
            factory.MakeChild += (_, e) => child = new TestChildResource(client, version, e.Id);
        });

        using var proxy = Bind<TestFactory>("test_factory", 2);
        using var childProxy = proxy.MakeChild();

        uint received = 0;
        childProxy.Poked += (_, e) => received = e.Value;

        PumpToServer();
        Assert.NotNull(child);

        child.SendPoked(31337);
        PumpToClient();

        Assert.Equal(31337u, received);
    }

    [Fact]
    public void A_roundtrip_completes()
    {
        using var registry = _client.GetRegistry();
        _client.Flush();
        _server.EventLoop.Dispatch(200);
        _server.FlushClients();

        // The sync the roundtrip sends has to come back with its callback done
        // and the id released, or the client waits for ever.
        var done = false;
        using var callback = _client.Sync();
        callback.Done += (_, _) => done = true;

        PumpToClient();
        Assert.True(done);
    }

    [Fact]
    public void A_file_descriptor_crosses_the_socket()
    {
        var path = Path.Combine(Path.GetTempPath(), $"waylandsharpest-fd-{Guid.NewGuid():N}");
        File.WriteAllText(path, "descriptor payload");

        try
        {
            var received = -1;
            using var global = _server.CreateGlobal(WlShm.Interface, 1, (client, version, id) =>
            {
                var shm = new WlShmResource(client, version, id);
                shm.CreatePool += (_, e) => received = e.Fd;
            });

            using var shmProxy = Bind<WlShm>("wl_shm", 1);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
            var fd = (int)handle.DangerousGetHandle();

            using var pool = shmProxy.CreatePool(fd, 16);
            PumpToServer();

            Assert.True(received > 0);

            // The server's copy is a descriptor of its own, and it names the
            // same file.
            Assert.NotEqual(fd, received);
            using var serverSide = new FileStream(
                new Microsoft.Win32.SafeHandles.SafeFileHandle(received, ownsHandle: true), FileAccess.Read);
            using var reader = new StreamReader(serverSide);
            Assert.Equal("descriptor payload", reader.ReadToEnd());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_peer_is_this_process()
    {
        var credentials = _serverClient.Credentials;
        Assert.Equal(Environment.ProcessId, credentials.Pid);
    }

    [Fact]
    public void A_protocol_error_reaches_the_client_as_one()
    {
        using var global = _server.CreateGlobal(TestFactory.Interface, 2, (client, version, id) =>
        {
            var factory = new TestFactoryResource(client, version, id);
            factory.MakeChild += (_, _) => factory.PostError(1, "no children today");
        });

        using var proxy = Bind<TestFactory>("test_factory", 2);
        using var childProxy = proxy.MakeChild();

        PumpToServer();
        _server.FlushClients();

        // libwayland turns the error event into a failed dispatch.
        Assert.ThrowsAny<Exception>(() => _client.Dispatch());
    }

    [Fact]
    public void The_client_goes_when_its_socket_closes()
    {
        var destroyed = false;
        _serverClient.Destroyed += () => destroyed = true;

        _client.Dispose();
        _server.EventLoop.Dispatch(200);

        Assert.True(destroyed);
        Assert.Empty(_server.Clients);
    }
}
