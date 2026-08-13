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

    bool SupportsLocalSocket => true;

    IWlEventLoop EventLoop { get; }

    string AddSocketAuto();

    void AddSocket(string name);

    /// <summary>Serves on an already-listening socket, taking ownership of <paramref name="fd"/>.</summary>
    /// <exception cref="NotSupportedException">The transport cannot adopt a listening socket.</exception>
    void AddSocketFd(int fd) =>
        throw new NotSupportedException($"{GetType().Name} does not support adopting a listening socket.");

    WlClient CreateClient(int fd);

    /// <summary>
    /// Creates a client served by <paramref name="transport"/>, taking ownership
    /// of it.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport cannot adopt a client transport.</exception>
    WlClient CreateClient(IWlClientTransport transport) =>
        throw new NotSupportedException($"{GetType().Name} does not accept client transports.");

    IWlGlobal CreateGlobal(WlGlobal owner, WlInterfaceSpec iface, int version);

    void Run();

    void Terminate();

    void FlushClients();

    /// <summary>Allocates the next event serial.</summary>
    uint NextSerial();

    void SetGlobalFilter(WlServerDisplay.GlobalFilter? filter);

    /// <summary>The currently connected clients, in connection order.</summary>
    /// <exception cref="NotSupportedException">The transport does not enumerate clients.</exception>
    IReadOnlyList<WlClient> GetClients() =>
        throw new NotSupportedException($"{GetType().Name} does not enumerate clients.");

    /// <summary>Whether this transport services the <c>wl_fixes</c> requests below.</summary>
    bool SupportsFixes => false;

    /// <summary>
    /// Services a client's <c>wl_fixes.ack_global_remove</c>.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport does not service wl_fixes.</exception>
    void AckGlobalRemove(WlClient client, nint fixesHandle, nint registryHandle, uint globalName) =>
        throw new NotSupportedException($"{GetType().Name} does not service wl_fixes.");

    /// <summary>
    /// Services a client's <c>wl_fixes.destroy_registry</c>: the registry
    /// object is destroyed and its id released.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport does not service wl_fixes.</exception>
    void DestroyRegistry(WlClient client, nint registryHandle) =>
        throw new NotSupportedException($"{GetType().Name} does not service wl_fixes.");

    /// <summary>
    /// Invoked when a client connects, before it has sent any request.
    /// Destroying the client from the handler rejects the connection.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport does not report client creation.</exception>
    Action<WlClient>? ClientCreatedHandler
    {
        get => throw new NotSupportedException($"{GetType().Name} does not report client creation.");
        set => throw new NotSupportedException($"{GetType().Name} does not report client creation.");
    }

    /// <summary>
    /// Logs every protocol message crossing this display until the returned
    /// registration is disposed.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport has no protocol logging.</exception>
    IDisposable AddProtocolLogger(WlProtocolLogger logger) =>
        throw new NotSupportedException($"{GetType().Name} does not support protocol logging.");
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

    /// <summary>The loop's pollable file descriptor; readable means work is pending.</summary>
    /// <exception cref="NotSupportedException">The transport's loop is not pollable.</exception>
    int Fd =>
        throw new NotSupportedException($"{GetType().Name} does not expose a pollable file descriptor.");

    /// <summary>Handles a POSIX signal on the loop thread. The callback must not throw.</summary>
    /// <exception cref="NotSupportedException">The transport does not deliver signals.</exception>
    IWlEventSource AddSignal(int signalNumber, Action<int> callback) =>
        throw new NotSupportedException($"{GetType().Name} does not support signal sources.");

    /// <summary>Runs pending idle callbacks without waiting for events.</summary>
    /// <exception cref="NotSupportedException">The transport has no idle queue to drain.</exception>
    void DispatchIdle() =>
        throw new NotSupportedException($"{GetType().Name} does not support dispatching idle callbacks.");
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

    /// <summary>
    /// The token table this client's fd-slot values are minted from, or null
    /// when they are kernel file descriptors.
    /// </summary>
    IFdSlotTable? FdSlots => null;

    /// <summary>Transport handle of the client's object <paramref name="id"/>, or 0.</summary>
    /// <exception cref="NotSupportedException">The transport does not support object lookup.</exception>
    nint GetObjectHandle(uint id) =>
        throw new NotSupportedException($"{GetType().Name} does not support object lookup by id.");

    /// <summary>
    /// Releases an fd-slot value this client delivered in a request, or that the
    /// compositor staged for an event and did not send.
    /// </summary>
    void CloseFd(int fd);
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

    /// <summary>
    /// Unpublishes the global and notifies clients, without destroying it.
    /// Requests already in flight still resolve.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport cannot unpublish without destroying.</exception>
    void Remove() =>
        throw new NotSupportedException($"{GetType().Name} does not support removing a global without destroying it.");

    /// <summary>Whether this transport can report <see cref="WithdrawnHandler"/>.</summary>
    bool SupportsWithdrawn => false;

    /// <summary>
    /// Invoked when no client can still bind this global and it is safe to
    /// dispose. Declared as a settable delegate rather than an event so the
    /// default implementation can throw.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport does not report withdrawal.</exception>
    Action? WithdrawnHandler
    {
        get => throw new NotSupportedException($"{GetType().Name} does not report global withdrawal.");
        set => throw new NotSupportedException($"{GetType().Name} does not report global withdrawal.");
    }
}
