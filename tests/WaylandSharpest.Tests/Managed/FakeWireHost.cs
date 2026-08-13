using System.Runtime.InteropServices;
using Wayland;
using Wayland.Native;
using Wayland.Server.Managed;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// An object model without a display: enough of one for the reader to resolve
/// arguments against, and a record of what it dispatched.
/// </summary>
internal sealed unsafe class FakeWireHost : IWlWireHost
{
    private readonly Dictionary<uint, (WlInterfaceSpec Spec, uint Version)> _objects = [];

    /// <summary>Requests the reader delivered, with argument storage already copied out.</summary>
    internal List<DispatchedRequest> Dispatched { get; } = [];

    internal List<int> ClosedFds { get; } = [];

    public uint MaxObjectId { get; set; }

    internal void AddObject(uint id, WlInterfaceSpec spec, uint version = 1) =>
        _objects[id] = (spec, version);

    internal void RemoveObject(uint id) => _objects.Remove(id);

    /// <summary>Handles are the id in the high half, so they are distinct from an id.</summary>
    private static nint HandleOf(uint id) => (nint)(0x1_0000_0000L | id);

    internal static uint IdOfHandle(nint handle) => (uint)(handle & 0xffffffff);

    public WlWireSignature BeginRequest(uint objectId, uint opcode)
    {
        if (!_objects.TryGetValue(objectId, out var entry))
        {
            throw new WlProtocolViolationException(
                objectId, WlDisplayError.InvalidObject, $"No object {objectId}.");
        }

        if (opcode >= entry.Spec.Requests.Count)
        {
            throw new WlProtocolViolationException(
                objectId, WlDisplayError.InvalidMethod, $"No request {opcode} on {entry.Spec.Name}.");
        }

        var signature = entry.Spec.Requests[(int)opcode].Wire;
        if (signature.SinceVersion > entry.Version)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidMethod,
                $"Request '{signature.Name}' needs version {signature.SinceVersion}, object is version {entry.Version}.");
        }

        return signature;
    }

    public bool TryResolveObject(uint id, out nint handle, out string interfaceName)
    {
        if (_objects.TryGetValue(id, out var entry))
        {
            handle = HandleOf(id);
            interfaceName = entry.Spec.Name;
            return true;
        }

        handle = 0;
        interfaceName = string.Empty;
        return false;
    }

    public bool IsObjectIdInUse(uint id) => _objects.ContainsKey(id);

    public void DispatchRequest(uint objectId, uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
        var values = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            values[i] = signature.Arguments[i].Code switch
            {
                's' => args[i].Ptr == 0 ? null : Marshal.PtrToStringUTF8(args[i].Ptr),
                'a' => ReadArray(args[i].Ptr),
                'o' => args[i].Ptr,
                'h' => args[i].Fd,
                'i' => args[i].I,
                _ => args[i].U,
            };
        }

        Dispatched.Add(new DispatchedRequest(objectId, opcode, signature.Name, values));
    }

    private static byte[] ReadArray(nint pointer)
    {
        if (pointer == 0)
        {
            return [];
        }

        var array = (wl_array*)pointer;
        var result = new byte[(int)array->size];
        if (result.Length > 0)
        {
            new ReadOnlySpan<byte>(array->data, result.Length).CopyTo(result);
        }

        return result;
    }

    public void CloseFd(int fd) => ClosedFds.Add(fd);
}

internal sealed record DispatchedRequest(uint ObjectId, uint Opcode, string Name, object?[] Args);
