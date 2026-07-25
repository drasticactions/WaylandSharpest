using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Cursor;

/// <summary>A named cursor from a <see cref="WlCursorTheme"/>: one or more animation frames.</summary>
public sealed unsafe class WlCursor
{
    private readonly WlCursorTheme _theme;
    private readonly wl_cursor* _handle;
    private readonly WlCursorImage[] _images;

    internal WlCursor(WlCursorTheme theme, wl_cursor* handle)
    {
        _theme = theme;
        _handle = handle;
        Name = Marshal.PtrToStringUTF8((nint)handle->name) ?? string.Empty;
        _images = new WlCursorImage[handle->image_count];
        for (var i = 0; i < _images.Length; i++)
        {
            _images[i] = new WlCursorImage(theme, handle->images[i]);
        }
    }

    /// <summary>The cursor's name within the theme.</summary>
    public string Name { get; }

    /// <summary>The animation frames; static cursors have exactly one.</summary>
    public IReadOnlyList<WlCursorImage> Images => _images;

    /// <summary>
    /// Returns the index into <see cref="Images"/> of the frame to show at
    /// <paramref name="time"/> milliseconds into the animation.
    /// </summary>
    public int Frame(uint time)
    {
        _theme.ThrowIfDisposed();
        return LibWaylandCursor.wl_cursor_frame(_handle, time);
    }

    /// <summary>
    /// Like <see cref="Frame"/>, additionally returning how long the frame
    /// remains current in milliseconds (0 for cursors that never change).
    /// </summary>
    public int FrameAndDuration(uint time, out uint duration)
    {
        _theme.ThrowIfDisposed();
        fixed (uint* durationPtr = &duration)
        {
            return LibWaylandCursor.wl_cursor_frame_and_duration(_handle, time, durationPtr);
        }
    }

    public override string ToString() => $"WlCursor({Name}, {_images.Length} frame(s))";
}
