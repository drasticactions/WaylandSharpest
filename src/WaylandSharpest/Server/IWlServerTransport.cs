namespace Wayland.Server;

/// <summary>
/// Interface for the server-side wire-protocol.
/// </summary>
public interface IWlServerTransport
{
    /// <summary>Creates the transport state behind a <see cref="WlServerDisplay"/>.</summary>
    IWlDisplay CreateDisplay(WlServerDisplay owner);
}

/// <summary>Transport half of a <see cref="WlServerDisplay"/>.</summary>
public interface IWlDisplay : IDisposable
{
    /// <summary>Native <c>wl_display*</c> for the libwayland transport; a transport-defined value otherwise.</summary>
    nint RawHandle { get; }

    IWlEventLoop EventLoop { get; }

    string AddSocketAuto();

    void AddSocket(string name);

    WlClient CreateClient(int fd);

    IWlGlobal CreateGlobal(WlGlobal owner, WlInterfaceSpec iface, int version);

    void Run();

    void Terminate();

    void FlushClients();
}

/// <summary>Transport half of a <see cref="WlEventLoop"/>.</summary>
public interface IWlEventLoop
{
    nint RawHandle { get; }

    /// <summary>Dispatches pending work; returns negative on failure.</summary>
    int Dispatch(int timeoutMs);
}

/// <summary>Transport half of a <see cref="WlClient"/>.</summary>
public interface IWlClient
{
    nint RawHandle { get; }

    void Flush();

    void Destroy();

    /// <summary>
    /// Creates the protocol object <paramref name="id"/> for this client and
    /// wires its incoming requests to <paramref name="owner"/> via
    /// <c>WlResource.DispatchIncoming</c>. The returned implementation's
    /// <see cref="IWlResource.RawHandle"/> must be process-unique for the
    /// resource's lifetime and must match the value the transport presents in
    /// <see cref="WlArg.Ptr"/> for object-typed arguments referring to it.
    /// </summary>
    IWlResource CreateResource(WlResource owner, WlInterfaceSpec spec, uint version, uint id);
}

/// <summary>Transport half of a <see cref="WlResource"/>.</summary>
public interface IWlResource
{
    nint RawHandle { get; }

    uint Id { get; }

    uint Version { get; }

    void PostEvent(uint opcode, ReadOnlySpan<WlArg> args);

    void PostError(uint code, string message);

    void Destroy();
}

/// <summary>Transport half of a <see cref="WlGlobal"/>; disposal withdraws the global.</summary>
public interface IWlGlobal : IDisposable
{
}
