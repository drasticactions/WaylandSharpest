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

    /// <summary>Allocates the next event serial.</summary>
    uint NextSerial();

    void SetGlobalFilter(WlServerDisplay.GlobalFilter? filter);
}

/// <summary>Transport half of a <see cref="WlEventLoop"/>.</summary>
public interface IWlEventLoop
{
    nint RawHandle { get; }

    /// <summary>Dispatches pending work; returns negative on failure.</summary>
    int Dispatch(int timeoutMs);

    /// <summary>Watches a file descriptor. The callback must not throw.</summary>
    IWlEventSource AddFd(int fd, WlFdEvents events, Action<int, WlFdEvents> callback);

    /// <summary>Adds a disarmed timer; arm it with <see cref="IWlEventSource.UpdateTimer"/>. The callback must not throw.</summary>
    IWlEventSource AddTimer(Action callback);

    /// <summary>Adds a one-shot idle callback, run before the loop next blocks. The callback must not throw.</summary>
    IWlEventSource AddIdle(Action callback);
}

/// <summary>A registered event-loop source.</summary>
public interface IWlEventSource
{
    /// <summary>True once removed, or after a one-shot source has fired.</summary>
    bool IsRemoved { get; }

    void Remove();

    /// <summary>Arms (delay in ms) or disarms (0) a timer source.</summary>
    void UpdateTimer(int delayMs);

    /// <summary>Changes the watched events of an fd source.</summary>
    void UpdateFd(WlFdEvents events);
}

/// <summary>Readiness mask for fd event sources (matches <c>WL_EVENT_*</c>).</summary>
[Flags]
public enum WlFdEvents : uint
{
    None = 0,
    Readable = 1,
    Writable = 2,
    Hangup = 4,
    Error = 8,
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

    /// <summary>The client's process and user identity.</summary>
    /// <exception cref="NotSupportedException">The transport does not expose peer credentials.</exception>
    WlClientCredentials GetCredentials() =>
        throw new NotSupportedException($"{GetType().Name} does not expose peer credentials.");

    /// <summary>The client's connection file descriptor.</summary>
    /// <exception cref="NotSupportedException">The transport is not socket-backed.</exception>
    int Fd =>
        throw new NotSupportedException($"{GetType().Name} does not expose a connection file descriptor.");

    /// <summary>Transport handle of the client's object <paramref name="id"/>, or 0.</summary>
    /// <exception cref="NotSupportedException">The transport does not support object lookup.</exception>
    nint GetObjectHandle(uint id) =>
        throw new NotSupportedException($"{GetType().Name} does not support object lookup by id.");
}

/// <summary>Peer credentials of a connected client, from <c>SO_PEERCRED</c>.</summary>
/// <param name="Pid">Process id of the peer.</param>
/// <param name="Uid">Effective user id of the peer.</param>
/// <param name="Gid">Effective group id of the peer.</param>
public readonly record struct WlClientCredentials(int Pid, uint Uid, uint Gid);

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
    /// <summary>
    /// The registry name this global is advertised under to
    /// <paramref name="client"/>, or 0 when the client cannot see it.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport does not expose registry global names.</exception>
    uint NameFor(WlClient client) =>
        throw new NotSupportedException($"{GetType().Name} does not expose registry global names.");

    /// <summary>The interface version this global was published at.</summary>
    /// <exception cref="NotSupportedException">The transport does not expose global versions.</exception>
    uint Version =>
        throw new NotSupportedException($"{GetType().Name} does not expose global versions.");
}
