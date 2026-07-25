namespace Wayland;

/// <summary>
/// Marks a generated client-side proxy class for a Wayland interface. The source
/// generator uses this attribute to resolve cross-protocol interface references
/// against already-compiled assemblies.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WaylandProxyAttribute : Attribute
{
    public WaylandProxyAttribute(string interfaceName) => InterfaceName = interfaceName;

    /// <summary>The wire-protocol interface name.</summary>
    public string InterfaceName { get; }
}

/// <summary>
/// Marks a generated server-side resource class for a Wayland interface.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WaylandResourceAttribute : Attribute
{
    public WaylandResourceAttribute(string interfaceName) => InterfaceName = interfaceName;

    /// <summary>The wire-protocol interface name.</summary>
    public string InterfaceName { get; }
}
