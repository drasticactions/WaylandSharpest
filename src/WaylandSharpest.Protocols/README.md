# WaylandSharpest.Protocols

This includes the [wayland-protocols](https://gitlab.freedesktop.org/wayland/wayland-protocols)
XML files, versioned so you can reference them through NuGet.

## Usage

Name the protocols you want:

```xml
<ItemGroup>
  <PackageReference Include="WaylandSharpest" Version="…" />
  <PackageReference Include="WaylandSharpest.Protocols" Version="…" />

  <WaylandProtocolFromPackage Include="xdg-shell;viewporter;cursor-shape-v1" />
</ItemGroup>
```

You can also set the `WaylandNamespace` metadata to control the generated namespace:

```xml
<WaylandProtocolFromPackage Include="xdg-shell" WaylandNamespace="MyCompositor.Protocols" />
```

## Licensing

The XML files are redistributed unmodified from upstream under their own terms;
the `protocols/COPYING` in this package carries them.
