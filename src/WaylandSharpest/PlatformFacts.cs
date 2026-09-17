namespace Wayland;

/// <summary>
/// The two kernel families the interop switches on, written once. The runtime
/// reports Android apart from Linux and iOS, Catalyst and tvOS apart from macOS,
/// while the libc facts below follow the kernel rather than the product.
/// </summary>
internal static class PlatformFacts
{
    /// <summary>A Linux kernel with glibc or Bionic: Linux proper and Android.</summary>
    internal static bool IsLinuxKernel { get; } =
        OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();

    /// <summary>A Darwin kernel with libSystem: macOS, iOS, Mac Catalyst and tvOS.</summary>
    internal static bool IsApple { get; } =
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ||
        OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS();
}
