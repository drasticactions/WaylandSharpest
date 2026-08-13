namespace Wayland.Server.Managed;

/// <summary>
/// The split of the object id space between the two ends of a connection. A
/// client allocates from the low range and the server from the high one, so
/// neither has to ask the other for an id.
/// </summary>
internal static class WlObjectIds
{
    /// <summary>The display object, which exists before any request is sent.</summary>
    internal const uint Display = 1;

    /// <summary>The highest id a client may allocate.</summary>
    internal const uint ClientIdMax = 0xfeffffff;

    /// <summary>The lowest id the server allocates.</summary>
    internal const uint ServerIdBase = 0xff000000;
}
