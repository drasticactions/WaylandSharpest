using Wayland.Native;

namespace Wayland.Egl;

/// <summary>
/// Managed wrapper for a <c>wl_egl_window</c>, the native window handle a
/// Wayland surface exposes to EGL. Pass <see cref="RawHandle"/> as the
/// <c>native_window</c> argument of <c>eglCreateWindowSurface</c> (via
/// Silk.NET, OpenTK, or your own EGL binding).
/// </summary>
public sealed unsafe class WlEglWindow : IDisposable
{
    private wl_egl_window* _handle;

    /// <summary>Creates an EGL window of the given size for <paramref name="surface"/>.</summary>
    public WlEglWindow(WlSurface surface, int width, int height)
    {
        _handle = LibWaylandEgl.wl_egl_window_create((wl_surface*)surface.RawHandle, width, height);
        if (_handle == null)
        {
            throw new WaylandException($"wl_egl_window_create failed for a {width}x{height} window.");
        }
    }

    /// <summary>The native <c>wl_egl_window*</c> handle to hand to <c>eglCreateWindowSurface</c>.</summary>
    public nint RawHandle
    {
        get
        {
            ThrowIfDestroyed();
            return (nint)_handle;
        }
    }

    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    public bool IsDestroyed => _handle == null;

    /// <summary>
    /// Schedules a resize; <paramref name="dx"/>/<paramref name="dy"/> move the
    /// surface relative to its old top-left corner so a window can grow to the
    /// left or up. Takes effect on the next buffer the EGL driver attaches.
    /// </summary>
    public void Resize(int width, int height, int dx = 0, int dy = 0)
    {
        ThrowIfDestroyed();
        LibWaylandEgl.wl_egl_window_resize(_handle, width, height, dx, dy);
    }

    /// <summary>The size of the buffer most recently attached by the EGL driver.</summary>
    public (int Width, int Height) GetAttachedSize()
    {
        ThrowIfDestroyed();
        int width, height;
        LibWaylandEgl.wl_egl_window_get_attached_size(_handle, &width, &height);
        return (width, height);
    }

    /// <summary>
    /// Destroys the native window. Destroy the EGL surface created from this
    /// window first, and only then the wl_surface it was created for.
    /// </summary>
    public void Dispose()
    {
        if (_handle == null)
        {
            return;
        }

        LibWaylandEgl.wl_egl_window_destroy(_handle);
        _handle = null;
    }

    private void ThrowIfDestroyed()
    {
        if (_handle == null)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }
}
