using Wayland.Native;

namespace Wayland.Cursor;

/// <summary>One animation frame of a <see cref="WlCursor"/>.</summary>
public sealed unsafe class WlCursorImage
{
    private readonly WlCursorTheme _theme;
    private readonly wl_cursor_image* _handle;
    private WlBuffer? _buffer;

    internal WlCursorImage(WlCursorTheme theme, wl_cursor_image* handle)
    {
        _theme = theme;
        _handle = handle;
        Width = handle->width;
        Height = handle->height;
        HotspotX = handle->hotspot_x;
        HotspotY = handle->hotspot_y;
        Delay = handle->delay;
    }

    /// <summary>Image width in pixels.</summary>
    public uint Width { get; }

    /// <summary>Image height in pixels.</summary>
    public uint Height { get; }

    /// <summary>Hotspot x coordinate, inside the image.</summary>
    public uint HotspotX { get; }

    /// <summary>Hotspot y coordinate, inside the image.</summary>
    public uint HotspotY { get; }

    /// <summary>Animation delay to the next frame in milliseconds.</summary>
    public uint Delay { get; }

    /// <summary>
    /// The frame's pixels as a <see cref="WlBuffer"/> ready to attach to a
    /// cursor surface. The buffer is borrowed from the theme: Dispose on it is
    /// a no-op, and it becomes unusable once the theme is disposed.
    /// </summary>
    public WlBuffer GetBuffer()
    {
        _theme.ThrowIfDisposed();
        if (_buffer is not null)
        {
            return _buffer;
        }

        var native = LibWaylandCursor.wl_cursor_image_get_buffer(_handle);
        if (native == null)
        {
            throw new WaylandException($"wl_cursor_image_get_buffer failed for a {Width}x{Height} cursor image.");
        }

        return _buffer = _theme.WrapBuffer(native);
    }
}
