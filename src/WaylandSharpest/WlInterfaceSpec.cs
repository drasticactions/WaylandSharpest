using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland;

/// <summary>
/// Managed description of a Wayland interface (name, version, request and event
/// signatures). Lazily materializes the native <c>struct wl_interface</c> graph
/// that libwayland needs for marshalling; native memory lives for the process
/// lifetime.
/// </summary>
public sealed unsafe class WlInterfaceSpec
{
    private static readonly object BuildLock = new();

    private wl_interface* _native;
    private readonly Func<nint, WlDisplay, WlProxy>? _proxyFactory;

    public WlInterfaceSpec(
        string name,
        int version,
        WlMessageSpec[] requests,
        WlMessageSpec[] events,
        Func<nint, WlDisplay, WlProxy>? proxyFactory = null)
    {
        Name = name;
        Version = version;
        Requests = requests;
        Events = events;
        _proxyFactory = proxyFactory;
    }

    /// <summary>The wire-protocol interface name.</summary>
    private bool _wiresRealized;

    /// <summary>Decodes every message signature, so no send pays for the first one on a frame.</summary>
    internal void RealizeWires()
    {
        if (_wiresRealized)
        {
            return;
        }

        _wiresRealized = true;
        for (var i = 0; i < Requests.Count; i++)
        {
            _ = Requests[i].Wire;
        }

        for (var i = 0; i < Events.Count; i++)
        {
            _ = Events[i].Wire;
        }
    }

    public string Name { get; }

    /// <summary>The most recent version of the interface described by the protocol XML.</summary>
    public int Version { get; }

    public IReadOnlyList<WlMessageSpec> Requests { get; }

    public IReadOnlyList<WlMessageSpec> Events { get; }

    /// <summary>Pointer to the native <c>struct wl_interface</c>, building it on first use.</summary>
    public nint NativeHandle => (nint)NativePointer;

    internal wl_interface* NativePointer
    {
        get
        {
            lock (BuildLock)
            {
                if (_native == null)
                {
                    var pending = new List<WlInterfaceSpec>();
                    AllocateHeaders(this, pending);
                    foreach (var spec in pending)
                    {
                        spec.FillMessages();
                    }
                }

                return _native;
            }
        }
    }

    internal WlProxy CreateProxy(nint handle, WlDisplay display)
    {
        if (_proxyFactory is null)
        {
            throw new WaylandException($"Interface '{Name}' has no proxy factory registered.");
        }

        return _proxyFactory(handle, display);
    }

    /// <summary>
    /// Phase one of the native build: allocate the <c>wl_interface</c> header for
    /// every spec reachable from <paramref name="spec"/> so that cyclic interface
    /// references can be resolved before any message arrays are filled in.
    /// </summary>
    private static void AllocateHeaders(WlInterfaceSpec spec, List<WlInterfaceSpec> pending)
    {
        if (spec._native != null)
        {
            return;
        }

        var native = (wl_interface*)Marshal.AllocHGlobal(sizeof(wl_interface));
        native->name = (sbyte*)Marshal.StringToHGlobalAnsi(spec.Name);
        native->version = spec.Version;
        native->method_count = 0;
        native->methods = null;
        native->event_count = 0;
        native->events = null;
        spec._native = native;
        pending.Add(spec);

        foreach (var message in spec.Requests)
        {
            AllocateReferencedHeaders(message, pending);
        }

        foreach (var message in spec.Events)
        {
            AllocateReferencedHeaders(message, pending);
        }
    }

    private static void AllocateReferencedHeaders(WlMessageSpec message, List<WlInterfaceSpec> pending)
    {
        foreach (var resolver in message.Types)
        {
            if (resolver is not null)
            {
                AllocateHeaders(resolver(), pending);
            }
        }
    }

    /// <summary>Phase two: fill in the request/event message arrays.</summary>
    private void FillMessages()
    {
        _native->methods = BuildMessages(Requests, out var methodCount);
        _native->method_count = methodCount;
        _native->events = BuildMessages(Events, out var eventCount);
        _native->event_count = eventCount;
    }

    private static wl_message* BuildMessages(IReadOnlyList<WlMessageSpec> messages, out int count)
    {
        count = messages.Count;
        if (count == 0)
        {
            return null;
        }

        var native = (wl_message*)Marshal.AllocHGlobal(sizeof(wl_message) * count);
        for (var i = 0; i < count; i++)
        {
            var message = messages[i];
            native[i].name = (sbyte*)Marshal.StringToHGlobalAnsi(message.Name);
            native[i].signature = (sbyte*)Marshal.StringToHGlobalAnsi(message.Signature);
            native[i].types = BuildTypes(message.Types);
        }

        return native;
    }

    private static wl_interface** BuildTypes(Func<WlInterfaceSpec>?[] resolvers)
    {
        if (resolvers.Length == 0)
        {
            return null;
        }

        var types = (wl_interface**)Marshal.AllocHGlobal(sizeof(wl_interface*) * resolvers.Length);
        for (var i = 0; i < resolvers.Length; i++)
        {
            // Referenced headers were allocated in phase one, so reading _native
            // directly here cannot recurse.
            types[i] = resolvers[i] is { } resolver ? resolver()._native : null;
        }

        return types;
    }

    public override string ToString() => $"{Name} v{Version}";
}

/// <summary>
/// A single request or event of a Wayland interface. <see cref="Signature"/> uses
/// the libwayland signature grammar (optional leading since-version digits, then
/// one letter per wire argument: i u f s o n a h, each optionally prefixed
/// with <c>?</c> for nullable).
/// </summary>
public sealed class WlMessageSpec
{
    private static readonly Func<WlInterfaceSpec>?[] EmptyTypes = [];

    public WlMessageSpec(string name, string signature, Func<WlInterfaceSpec>?[]? types = null)
    {
        Name = name;
        Signature = signature;
        Types = types ?? EmptyTypes;
    }

    public string Name { get; }

    public string Signature { get; }

    /// <summary>
    /// One entry per wire argument; non-null for typed <c>o</c>/<c>n</c> arguments.
    /// Deferred via delegates so that mutually referencing interfaces do not
    /// deadlock their static initializers.
    /// </summary>
    internal Func<WlInterfaceSpec>?[] Types { get; }

    internal int WireArgCount => Types.Length;

    private WlWireSignature? _wire;

    /// <summary>
    /// The decoded signature. Two threads racing here build equal instances, so
    /// the unsynchronized publish is harmless.
    /// </summary>
    internal WlWireSignature Wire => _wire ??= WlWireSignature.Parse(this);

    public override string ToString() => $"{Name}({Signature})";
}
