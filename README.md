# WaylandSharpest

WaylandSharpest, the _sharpest_ of Wayland.

This is (another) .NET binding for libwayland, with a Roslyn source generator that turns [wayland-protocol XML](https://gitlab.freedesktop.org/wayland/wayland-protocols) into idiomatic C# APIs.

## Consuming protocols

Add the package and drop protocol XML into a `wayland-protocols/` folder next to your project fil, where they will be picked up automatically, or declare items explicitly:

```xml
<ItemGroup>
  <PackageReference Include="WaylandSharpest" Version="..." />
  <WaylandProtocol Include="protocol/xdg-shell.xml" WaylandNamespace="My.Protocols" />
</ItemGroup>
```

The Generated code lands in your project's root namespace unless overridden with `WaylandNamespace`. References to interfaces from other protocols resolve against protocol XML in the same project first, then against referenced assemblies via the `[WaylandProxy]`/`[WaylandResource]` attributes.

### Client example

```csharp
using var display = WlDisplay.Connect();
using var registry = display.GetRegistry();

WlCompositor? compositor = null;
registry.Global += (_, e) =>
{
    if (e.Interface == "wl_compositor")
        compositor = registry.Bind<WlCompositor>(e.Name, e.Version);
};
display.Roundtrip();

using var surface = compositor!.CreateSurface();
surface.Commit();
```

There are no finalizers; Wayland objects must be destroyed from the dispatch thread, so leaks are your responsibility.

### Server example

```csharp
using var server = WlServerDisplay.Create();
var socket = server.AddSocketAuto();
using var global = server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
{
    var compositor = new WlCompositorResource(client, version, id);
    compositor.CreateSurface += (_, e) => new WlSurfaceResource(client, version, e.Id);
});
server.Run();
```

## Building and testing

```sh
dotnet build
dotnet test

# run the sample
weston --backend=headless --socket=demo &
WAYLAND_DISPLAY=demo dotnet run --project samples/ShmWindow
```

Regenerating the raw bindings after updating the wayland submodule requires the `ClangSharpPInvokeGenerator` dotnet tool and clang:

```sh
dotnet tool install --global ClangSharpPInvokeGenerator
eng/generate-native.sh
```