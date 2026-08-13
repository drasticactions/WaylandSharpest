using System.Runtime.InteropServices;
using Wayland;
using Wayland.Native;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// The accessors for resources another implementation on the same display
/// owns. A managed display has none: every object in its table is one of ours,
/// so there is nothing for these to reach and no native resource behind a
/// handle to reach it with.
/// </summary>
[LibWaylandOnly("WlForeignResource reads a libwayland wl_resource through its handle, and a managed display's handles do not name one.")]
public sealed class ForeignResourceTests : LoopbackHarness
{
    private WlCompositor BindCompositor(WlGlobal global) => Bind<WlCompositor>("wl_compositor", 6);

    [Fact]
    public void Foreign_resource_argument_decodes_to_null_with_usable_handle()
    {
        // Simulates a resource owned by another native library (e.g. wlroots):
        // created behind WaylandSharpest's back, with user_data pointing at a
        // plain heap allocation rather than a GCHandle.
        var foreignUserData = Marshal.AllocHGlobal(16);
        nint foreignRegion = 0;
        var setInputRegionRaised = false;
        WlRegionResource? decodedRegion = null;
        nint decodedHandle = 0;

        try
        {
            using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
            {
                var compositor = new WlCompositorResource(client, version, id);
                compositor.CreateSurface += (_, e) =>
                {
                    var serverSurface = new WlSurfaceResource(client, version, e.Id);
                    serverSurface.SetInputRegion += (_, e) =>
                    {
                        setInputRegionRaised = true;
                        decodedRegion = e.Region;
                        decodedHandle = e.RegionHandle;
                    };
                };
                compositor.CreateRegion += (_, e) =>
                {
                    unsafe
                    {
                        var resource = LibWaylandServer.wl_resource_create(
                            (wl_client*)client.RawHandle,
                            (wl_interface*)WlRegion.Interface.NativeHandle,
                            (int)version,
                            e.Id);
                        LibWaylandServer.wl_resource_set_user_data(resource, (void*)foreignUserData);
                        foreignRegion = (nint)resource;
                    }
                };
            });

            using var compositor = BindCompositor(global);
            using var surface = compositor.CreateSurface();
            // Not disposed: the foreign resource has no request handlers, so the
            // destructor request must never reach it.
            var region = compositor.CreateRegion();
            surface.SetInputRegion(region);
            PumpToServer();

            Assert.True(setInputRegionRaised);
            Assert.NotEqual(0, foreignRegion);
            Assert.Null(decodedRegion);
            Assert.Equal(foreignRegion, decodedHandle);

            // The wrapper decodes to null, but the handle is still identifiable
            // and traceable back to its client.
            Assert.Equal("wl_region", WlForeignResource.GetInterfaceName(foreignRegion));
            Assert.True(WlForeignResource.IsInstanceOf(foreignRegion, WlRegion.Interface));
            Assert.False(WlForeignResource.IsInstanceOf(foreignRegion, WlSurface.Interface));
            Assert.Same(ServerClient, WlForeignResource.GetClient(Server, foreignRegion));
        }
        finally
        {
            Marshal.FreeHGlobal(foreignUserData);
        }
    }

    [Fact]
    public void Owned_resources_answer_the_foreign_accessors_too()
    {
        WlSurfaceResource? serverSurface = null;
        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) => serverSurface = new WlSurfaceResource(client, version, e.Id);
        });

        using var compositor = BindCompositor(global);
        using var surface = compositor.CreateSurface();
        PumpToServer();

        Assert.NotNull(serverSurface);
        var handle = serverSurface!.RawHandle;
        Assert.Equal("wl_surface", WlForeignResource.GetInterfaceName(handle));
        Assert.True(WlForeignResource.IsInstanceOf(handle, WlSurface.Interface));
        Assert.Same(ServerClient, WlForeignResource.GetClient(Server, handle));
    }
}
