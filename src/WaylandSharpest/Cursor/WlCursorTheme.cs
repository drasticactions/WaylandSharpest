using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Cursor;

/// <summary>
/// A loaded Xcursor theme, wrapping <c>wl_cursor_theme</c>. The theme owns
/// every <see cref="WlCursor"/>, <see cref="WlCursorImage"/>, and buffer it
/// hands out; disposing the theme invalidates them all.
/// </summary>
public sealed unsafe class WlCursorTheme : IDisposable
{
    private readonly Dictionary<string, WlCursor?> _cursors = [];
    private readonly List<WlBuffer> _buffers = [];
    private wl_cursor_theme* _handle;

    private WlCursorTheme(wl_cursor_theme* handle, WlDisplay display)
    {
        _handle = handle;
        Display = display;
    }

    /// <summary>The connection the theme's buffers belong to.</summary>
    public WlDisplay Display { get; }

    /// <summary>True once the theme has been disposed.</summary>
    public bool IsDisposed => _handle == null;

    /// <summary>
    /// Loads a cursor theme with images of the given <paramref name="size"/> in
    /// pixels. A null <paramref name="name"/> selects the default theme
    /// (<c>XCURSOR_THEME</c> or "default"). Cursor pixels are shared with the
    /// compositor through <paramref name="shm"/>.
    /// </summary>
    public static WlCursorTheme Load(string? name, int size, WlShm shm)
    {
        var namePtr = name is null ? 0 : Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var theme = LibWaylandCursor.wl_cursor_theme_load((sbyte*)namePtr, size, (wl_shm*)shm.RawHandle);
            if (theme == null)
            {
                throw new WaylandException($"Failed to load cursor theme '{name ?? "(default)"}' at size {size}.");
            }

            return new WlCursorTheme(theme, shm.Display);
        }
        finally
        {
            if (namePtr != 0)
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }
    }

    /// <summary>Returns the named cursor, or null when the theme does not contain it.</summary>
    public WlCursor? GetCursor(string name)
    {
        ThrowIfDisposed();
        if (_cursors.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var native = LibWaylandCursor.wl_cursor_theme_get_cursor(_handle, (sbyte*)namePtr);
            var cursor = native == null ? null : new WlCursor(this, native);
            _cursors[name] = cursor;
            return cursor;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    /// <summary>Destroys the theme and every cursor, image, and buffer it handed out.</summary>
    public void Dispose()
    {
        if (_handle == null)
        {
            return;
        }

        // The native theme owns the wl_buffers and destroys them below;
        // invalidate the borrowed wrappers first.
        foreach (var buffer in _buffers)
        {
            buffer.ReleaseBorrowed();
        }

        _buffers.Clear();
        _cursors.Clear();

        LibWaylandCursor.wl_cursor_theme_destroy(_handle);
        _handle = null;
    }

    internal void ThrowIfDisposed()
    {
        if (_handle == null)
        {
            throw new ObjectDisposedException(nameof(WlCursorTheme));
        }
    }

    internal WlBuffer WrapBuffer(wl_buffer* native)
    {
        var buffer = (WlBuffer)WlProxy.CreateBorrowed(WlBuffer.Interface, (nint)native, Display);
        _buffers.Add(buffer);
        return buffer;
    }
}
