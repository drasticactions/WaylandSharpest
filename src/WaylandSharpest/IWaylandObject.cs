namespace Wayland;

/// <summary>
/// Implemented by generated proxy classes to expose their interface metadata
/// statically, enabling generic helpers such as
/// <c>WlRegistry.Bind&lt;T&gt;(name, version)</c>.
/// </summary>
public interface IWaylandObject<TSelf> where TSelf : IWaylandObject<TSelf>
{
    /// <summary>Interface metadata for this protocol object type.</summary>
    static abstract WlInterfaceSpec Interface { get; }
}
