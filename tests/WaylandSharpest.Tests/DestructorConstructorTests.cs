using Wayland;
using WaylandSharpest.Tests.Protocol;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// A <c>type="destructor"</c> request carrying a <c>new_id</c> both creates the
/// child and destroys the sender, in one wire call. Three interfaces in
/// wayland-protocols depend on it (color-management, drm-lease).
/// </summary>
public sealed class DestructorConstructorTests : LoopbackHarness
{
    /// <summary>Exposes the protected proxy-registry lookup.</summary>
    private sealed class ProxyProbe : WlProxy
    {
        private ProxyProbe() : base(0, null)
        {
        }

        protected override WlInterfaceSpec Spec => throw new NotSupportedException();

        protected override void HandleEvent(uint opcode, ReadOnlySpan<WlArg> args) => throw new NotSupportedException();

        public static T? Decode<T>(nint handle) where T : WlProxy => GetProxy<T>(new WlArg { Ptr = handle });
    }

    [Fact]
    public void Destructor_constructor_destroys_the_parent_proxy()
    {
        TestFactoryResource? factory = null;
        TestChildResource? child = null;
        var factoryDestroyed = false;
        uint poked = 0;

        using var global = Server.CreateGlobal(TestFactory.Interface, 2, (client, version, id) =>
        {
            factory = new TestFactoryResource(client, version, id);
            factory.Destroyed += (_, _) => factoryDestroyed = true;
            factory.Convert += (_, e) =>
            {
                child = new TestChildResource(client, version, e.Id);
                child.Poke += (_, p) => poked = p.Value;
            };
        });

        var proxy = Bind<TestFactory>("test_factory", 2);
        var factoryHandle = proxy.RawHandle;

        var converted = proxy.Convert();

        Assert.True(proxy.IsDestroyed);
        Assert.Null(ProxyProbe.Decode<TestFactory>(factoryHandle));
        Assert.Throws<ObjectDisposedException>(() => proxy.RawHandle);

        // The child returned by the same call is a live, usable object.
        converted.Poke(42);
        PumpToServer();

        Assert.True(factoryDestroyed);
        Assert.NotNull(child);
        Assert.Equal(42u, poked);

        converted.Dispose();
        PumpToServer();
        Assert.True(child!.IsDestroyed);
    }

    [Fact]
    public void Plain_constructor_leaves_the_parent_alive()
    {
        using var global = Server.CreateGlobal(TestFactory.Interface, 2, (client, version, id) =>
        {
            var factory = new TestFactoryResource(client, version, id);
            factory.MakeChild += (_, e) => _ = new TestChildResource(client, version, e.Id);
        });

        using var proxy = Bind<TestFactory>("test_factory", 2);
        using var child = proxy.MakeChild();
        PumpToServer();

        Assert.False(proxy.IsDestroyed);
        Assert.False(child.IsDestroyed);
    }
}
