using Wayland;
using Wayland.Server;
using WaylandSharpest.Tests.Protocol;
using Xunit;
using static WaylandSharpest.Tests.Managed.ScriptedWireClient;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// A managed display driven by a hand-written peer: the bootstrap a client
/// performs before it can do anything, then objects, events and teardown.
/// Nothing here touches libwayland, so it runs wherever .NET does.
/// </summary>
public sealed class ManagedDisplayTests : IDisposable
{
    private const uint DisplayId = 1;
    private const uint SyncOpcode = 0;
    private const uint GetRegistryOpcode = 1;
    private const uint ErrorOpcode = 0;
    private const uint DeleteIdOpcode = 1;
    private const uint BindOpcode = 0;
    private const uint GlobalOpcode = 0;
    private const uint GlobalRemoveOpcode = 1;

    private const uint RegistryId = 2;
    private const uint FactoryId = 3;
    private const uint ChildId = 4;

    private readonly WlServerDisplay _display = WlServerDisplay.Create(new ManagedTransport());
    private readonly FakeClientTransport _transport = new();
    private readonly ScriptedWireClient _wire;

    public ManagedDisplayTests()
    {
        Client = _display.CreateClient(_transport);
        _wire = new ScriptedWireClient(_transport);
    }

    private WlClient Client { get; }

    public void Dispose() => _display.Dispose();

    private void Pump() => _display.EventLoop.Dispatch(0);

    private WlGlobal PublishFactory(Action<TestFactoryResource>? configure = null, int version = 2) =>
        _display.CreateGlobal(TestFactory.Interface, version, (client, boundVersion, id) =>
        {
            var factory = new TestFactoryResource(client, boundVersion, id);
            configure?.Invoke(factory);
        });

    /// <summary>Gets a registry and returns the registry name the global landed on.</summary>
    private uint BootstrapRegistry(string interfaceName)
    {
        _wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId));
        Pump();

        var advertised = _wire.Drain()
            .Where(e => e.ObjectId == RegistryId && e.Opcode == GlobalOpcode)
            .Single(e => e.StringAt(4) == interfaceName);
        return advertised.UInt32At(0);
    }

    private void Bind(uint name, string interfaceName, uint version, uint newId)
    {
        _wire.Send(RegistryId, BindOpcode, Concat(U32(name), Str(interfaceName), U32(version), U32(newId)));
        Pump();
    }

    [Fact]
    public void A_registry_is_told_about_the_globals_that_exist()
    {
        using var global = PublishFactory();

        _wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId));
        Pump();

        var advertised = Assert.Single(_wire.Drain());
        Assert.Equal(RegistryId, advertised.ObjectId);
        Assert.Equal(GlobalOpcode, advertised.Opcode);
        Assert.Equal("test_factory", advertised.StringAt(4));
        Assert.Equal(2u, advertised.UInt32At(advertised.AfterStringAt(4)));
    }

    [Fact]
    public void A_global_published_later_reaches_a_registry_that_already_exists()
    {
        _wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId));
        Pump();
        Assert.Empty(_wire.Drain());

        using var global = PublishFactory();
        _display.FlushClients();

        var advertised = Assert.Single(_wire.Drain());
        Assert.Equal("test_factory", advertised.StringAt(4));
    }

    [Fact]
    public void Binding_a_global_runs_its_handler_with_the_requested_version()
    {
        TestFactoryResource? bound = null;
        using var global = PublishFactory(factory => bound = factory);

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 1, FactoryId);

        Assert.NotNull(bound);
        Assert.Equal(FactoryId, bound.Id);
        Assert.Equal(1u, bound.Version);
    }

    [Fact]
    public void A_request_reaches_the_resource_that_owns_the_object()
    {
        TestChildResource? child = null;
        uint poked = 0;
        using var global = PublishFactory(factory => factory.MakeChild += (_, e) =>
        {
            child = new TestChildResource(factory.Client, factory.Version, e.Id);
            child.Poke += (_, p) => poked = p.Value;
        });

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);

        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();
        Assert.NotNull(child);

        _wire.Send(ChildId, 1, U32(99));
        Pump();
        Assert.Equal(99u, poked);
    }

    [Fact]
    public void An_event_reaches_the_wire_with_its_arguments()
    {
        TestChildResource? child = null;
        using var global = PublishFactory(factory => factory.MakeChild += (_, e) =>
            child = new TestChildResource(factory.Client, factory.Version, e.Id));

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);
        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();
        _wire.Drain();

        child!.SendPoked(1234);
        _display.FlushClients();

        var poked = Assert.Single(_wire.Drain());
        Assert.Equal(ChildId, poked.ObjectId);
        Assert.Equal(0u, poked.Opcode);
        Assert.Equal(1234u, poked.UInt32At(0));
    }

    [Fact]
    public void A_sync_is_answered_and_its_callback_deleted()
    {
        _wire.Send(DisplayId, SyncOpcode, U32(7));
        Pump();

        var events = _wire.Drain();
        Assert.Equal(2, events.Count);

        Assert.Equal(7u, events[0].ObjectId);
        Assert.Equal(0u, events[0].Opcode);

        Assert.Equal(DisplayId, events[1].ObjectId);
        Assert.Equal(DeleteIdOpcode, events[1].Opcode);
        Assert.Equal(7u, events[1].UInt32At(0));
    }

    [Fact]
    public void Two_syncs_carry_rising_serials()
    {
        _wire.Send(DisplayId, SyncOpcode, U32(7));
        _wire.Send(DisplayId, SyncOpcode, U32(8));
        Pump();

        var done = _wire.Drain().Where(e => e.ObjectId is 7 or 8).ToArray();
        Assert.Equal(2, done.Length);
        Assert.True(done[1].UInt32At(0) > done[0].UInt32At(0));
    }

    [Fact]
    public void A_destructor_request_deletes_the_object_id()
    {
        TestChildResource? child = null;
        var destroyed = false;
        using var global = PublishFactory(factory => factory.MakeChild += (_, e) =>
        {
            child = new TestChildResource(factory.Client, factory.Version, e.Id);
            child.Destroyed += (_, _) => destroyed = true;
        });

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);
        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();
        _wire.Drain();

        _wire.Send(ChildId, 0);
        Pump();

        Assert.True(destroyed);
        Assert.True(child!.IsDestroyed);

        var deleted = Assert.Single(_wire.Drain());
        Assert.Equal(DisplayId, deleted.ObjectId);
        Assert.Equal(DeleteIdOpcode, deleted.Opcode);
        Assert.Equal(ChildId, deleted.UInt32At(0));
    }

    [Fact]
    public void An_id_freed_by_a_destructor_can_be_used_again()
    {
        var children = new List<TestChildResource>();
        using var global = PublishFactory(factory => factory.MakeChild += (_, e) =>
            children.Add(new TestChildResource(factory.Client, factory.Version, e.Id)));

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);

        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();
        _wire.Send(ChildId, 0);
        Pump();
        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();

        Assert.Equal(2, children.Count);
        Assert.False(children[1].IsDestroyed);
    }

    [Fact]
    public void Binding_an_unknown_name_is_a_protocol_error()
    {
        _wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId));
        Pump();
        _wire.Drain();

        Bind(404, "test_factory", 1, FactoryId);

        var error = Assert.Single(_wire.Drain());
        Assert.Equal(DisplayId, error.ObjectId);
        Assert.Equal(ErrorOpcode, error.Opcode);
        Assert.True(Client.IsDestroyed);
    }

    [Fact]
    public void Binding_above_the_advertised_version_is_a_protocol_error()
    {
        using var global = PublishFactory(version: 1);
        var name = BootstrapRegistry("test_factory");
        _wire.Drain();

        Bind(name, "test_factory", 2, FactoryId);

        var error = Assert.Single(_wire.Drain());
        Assert.Equal(ErrorOpcode, error.Opcode);
    }

    [Fact]
    public void Binding_under_the_wrong_interface_is_a_protocol_error()
    {
        using var global = PublishFactory();
        var name = BootstrapRegistry("test_factory");
        _wire.Drain();

        Bind(name, "test_child", 1, FactoryId);

        Assert.Equal(ErrorOpcode, Assert.Single(_wire.Drain()).Opcode);
    }

    [Fact]
    public void A_request_at_a_version_the_object_does_not_have_is_a_protocol_error()
    {
        using var global = PublishFactory();
        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 1, FactoryId);
        _wire.Drain();

        // poke_v2 exists only from version 2, and the object was bound at 1.
        _wire.Send(FactoryId, 3);
        Pump();

        Assert.Equal(ErrorOpcode, Assert.Single(_wire.Drain()).Opcode);
        Assert.True(Client.IsDestroyed);
    }

    [Fact]
    public void A_request_to_an_unknown_object_is_a_protocol_error()
    {
        _wire.Send(999, 0);
        Pump();

        Assert.Equal(ErrorOpcode, Assert.Single(_wire.Drain()).Opcode);
    }

    [Fact]
    public void A_removed_global_stops_being_advertised_but_still_binds()
    {
        TestFactoryResource? bound = null;
        var global = PublishFactory(factory => bound = factory);

        var name = BootstrapRegistry("test_factory");
        _wire.Drain();

        global.Remove();
        _display.FlushClients();

        var removal = Assert.Single(_wire.Drain());
        Assert.Equal(GlobalRemoveOpcode, removal.Opcode);
        Assert.Equal(name, removal.UInt32At(0));

        // A bind already on its way still has to be answered.
        Bind(name, "test_factory", 2, FactoryId);
        Assert.NotNull(bound);

        global.Dispose();
    }

    [Fact]
    public void A_second_registry_is_told_about_the_same_globals()
    {
        using var global = PublishFactory();

        _wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId));
        _wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId + 10));
        Pump();

        var advertised = _wire.Drain().Where(e => e.Opcode == GlobalOpcode).ToArray();
        Assert.Equal(2, advertised.Length);
        Assert.Equal(RegistryId, advertised[0].ObjectId);
        Assert.Equal(RegistryId + 10, advertised[1].ObjectId);
    }

    [Fact]
    public void A_filter_hides_a_global_from_a_client()
    {
        using var global = PublishFactory();
        _display.SetGlobalFilter((_, _, interfaceName) => interfaceName != "test_factory");

        _wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId));
        Pump();

        Assert.Empty(_wire.Drain());
        Assert.Equal(0u, global.NameFor(Client));
    }

    [Fact]
    public void A_hidden_global_cannot_be_bound()
    {
        using var global = PublishFactory();
        var name = BootstrapRegistry("test_factory");
        _wire.Drain();

        _display.SetGlobalFilter((_, _, interfaceName) => interfaceName != "test_factory");
        Bind(name, "test_factory", 1, FactoryId);

        Assert.Equal(ErrorOpcode, Assert.Single(_wire.Drain()).Opcode);
    }

    [Fact]
    public void The_client_list_tracks_connections()
    {
        Assert.Same(Client, Assert.Single(_display.Clients));

        Client.Destroy();
        Pump();

        Assert.Empty(_display.Clients);
        Assert.Equal(1, _transport.Disposals);
    }

    [Fact]
    public void A_new_connection_is_reported_before_it_speaks()
    {
        WlClient? reported = null;
        _display.ClientCreated += client => reported = client;

        var second = new FakeClientTransport();
        var created = _display.CreateClient(second);

        Assert.Same(created, reported);
    }

    [Fact]
    public void End_of_stream_disconnects_the_client()
    {
        var destroyed = false;
        Client.Destroyed += () => destroyed = true;

        _transport.EndOfStream = true;
        Pump();

        Assert.True(destroyed);
        Assert.True(Client.IsDestroyed);
    }

    [Fact]
    public void Resources_are_destroyed_when_their_client_goes()
    {
        TestChildResource? child = null;
        using var global = PublishFactory(factory => factory.MakeChild += (_, e) =>
            child = new TestChildResource(factory.Client, factory.Version, e.Id));

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);
        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();

        Client.Destroy();
        Pump();

        Assert.True(child!.IsDestroyed);
    }

    [Fact]
    public void A_handler_that_throws_does_not_kill_the_connection()
    {
        using var global = PublishFactory(factory => factory.MakeChild += (_, _) =>
            throw new InvalidOperationException("handler trouble"));

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);

        _wire.Send(FactoryId, 0, U32(ChildId));

        var thrown = Assert.Throws<WaylandException>(Pump);
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.False(Client.IsDestroyed);
    }

    [Fact]
    public void A_protocol_logger_sees_both_directions()
    {
        var seen = new List<string>();
        TestChildResource? child = null;
        using var global = PublishFactory(factory => factory.MakeChild += (_, e) =>
            child = new TestChildResource(factory.Client, factory.Version, e.Id));

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);

        using (_display.AddProtocolLogger((in WlProtocolMessage message) =>
            seen.Add($"{message.Direction} {message.InterfaceName}.{message.MessageName}")))
        {
            _wire.Send(FactoryId, 0, U32(ChildId));
            Pump();
            child!.SendPoked(5);
            _display.FlushClients();
        }

        Assert.Contains("Request test_factory.make_child", seen);
        Assert.Contains("Event test_child.poked", seen);
    }

    [Fact]
    public void Nested_dispatch_is_refused()
    {
        Exception? captured = null;
        using var global = PublishFactory(factory => factory.MakeChild += (_, _) =>
        {
            try
            {
                _display.EventLoop.Dispatch(0);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);

        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();

        Assert.IsType<InvalidOperationException>(captured);
    }

    [Fact]
    public void A_client_destroyed_inside_its_own_handler_tears_down_afterwards()
    {
        using var global = PublishFactory(factory => factory.MakeChild += (_, _) => factory.Client.Destroy());

        var name = BootstrapRegistry("test_factory");
        Bind(name, "test_factory", 2, FactoryId);

        _wire.Send(FactoryId, 0, U32(ChildId));
        Pump();

        Assert.True(Client.IsDestroyed);
        Assert.Equal(1, _transport.Disposals);
    }
}
