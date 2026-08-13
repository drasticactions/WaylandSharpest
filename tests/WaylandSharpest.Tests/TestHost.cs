using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// What this host can and cannot run. The managed transport is meant to work
/// where libwayland does not exist, so its tests have to be able to say when a
/// libwayland client is unavailable rather than fail for want of one.
/// </summary>
internal static class TestHost
{
    private static readonly Lazy<bool> LibwaylandProbe = new(() =>
    {
        try
        {
            return System.Runtime.InteropServices.NativeLibrary.TryLoad("libwayland-client.so.0", out _);
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>Whether a libwayland client can be created here.</summary>
    internal static bool HasLibwayland => LibwaylandProbe.Value;

    /// <summary>Whether descriptors can be passed over a socket here.</summary>
    internal static bool HasFdPassingSockets => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    internal static void SkipWithoutLibwayland()
    {
        if (!HasLibwayland)
        {
            Assert.Skip("libwayland is not installed here, so there is no client to talk to.");
        }
    }

    internal static void SkipWithoutFdPassingSockets()
    {
        if (!HasFdPassingSockets)
        {
            Assert.Skip("This host has no socket that can pass file descriptors.");
        }
    }
}
