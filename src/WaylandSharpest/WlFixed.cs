using System.Runtime.InteropServices;

namespace Wayland;

/// <summary>
/// Wayland signed 24.8 fixed-point number (<c>wl_fixed_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct WlFixed : IEquatable<WlFixed>
{
    /// <summary>The raw 24.8 fixed-point bits.</summary>
    public int Raw { get; }

    public WlFixed(int raw) => Raw = raw;

    public static WlFixed FromDouble(double value) => new((int)Math.Round(value * 256.0));

    public static WlFixed FromInt(int value) => new(value * 256);

    public double ToDouble() => Raw / 256.0;

    public int ToInt() => Raw / 256;

    public static implicit operator double(WlFixed f) => f.ToDouble();

    public static explicit operator WlFixed(double value) => FromDouble(value);

    public bool Equals(WlFixed other) => Raw == other.Raw;

    public override bool Equals(object? obj) => obj is WlFixed f && Equals(f);

    public override int GetHashCode() => Raw;

    public static bool operator ==(WlFixed left, WlFixed right) => left.Equals(right);

    public static bool operator !=(WlFixed left, WlFixed right) => !left.Equals(right);

    public override string ToString() => ToDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
}
