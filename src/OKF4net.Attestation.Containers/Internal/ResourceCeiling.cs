// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Validation for the four resource ceilings a profile carries. These are a
/// security control, not a tuning knob, and the design is explicit that an
/// invalid or accidentally-unlimited configuration must be rejected rather than
/// silently ignored.
///
/// The reason it cannot be left to the engine: the values are passed straight
/// through to <c>--memory</c>, <c>--cpus</c> and <c>--pids-limit</c>, and to
/// docker and podman a zero or negative value there does not mean "invalid",
/// it means <b>unlimited</b>. A profile written with <c>MemoryBytes = 0</c>
/// would therefore remove the exact ceiling it looks like it is setting, and do
/// it quietly. A non-positive <c>Timeout</c> fails differently but just as
/// unhelpfully — as a raw <see cref="ArgumentOutOfRangeException"/> from a
/// <see cref="System.Threading.CancellationTokenSource"/> constructor, at run
/// time, naming neither the profile nor the property.
/// </summary>
internal static class ResourceCeiling
{
    /// <summary>Returns <paramref name="value"/> if it is positive; otherwise throws naming <paramref name="property"/>.</summary>
    internal static long Positive(long value, string property) =>
        value > 0 ? value : throw Rejected(value, property);

    /// <summary>Returns <paramref name="value"/> if it is positive; otherwise throws naming <paramref name="property"/>.</summary>
    internal static double Positive(double value, string property) =>
        value > 0 ? value : throw Rejected(value, property);

    /// <summary>Returns <paramref name="value"/> if it is positive; otherwise throws naming <paramref name="property"/>.</summary>
    internal static int Positive(int value, string property) =>
        value > 0 ? value : throw Rejected(value, property);

    /// <summary>Returns <paramref name="value"/> if it is a positive duration; otherwise throws naming <paramref name="property"/>.</summary>
    internal static TimeSpan Positive(TimeSpan value, string property) =>
        value > TimeSpan.Zero ? value : throw Rejected(value, property);

    private static ArgumentOutOfRangeException Rejected(object value, string property) =>
        new(property, value, $"{property} must be positive; '{value}' would remove the ceiling rather than set one.");
}
