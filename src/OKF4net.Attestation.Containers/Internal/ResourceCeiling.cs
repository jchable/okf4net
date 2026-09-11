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
/// it quietly.
///
/// The wall-clock ceiling fails differently, which is why it has its own check
/// and its own message. A <see cref="System.Threading.CancellationTokenSource"/>
/// <i>accepts</i> <c>Timeout.InfiniteTimeSpan</c> (-1 ms) and simply never fires —
/// the ceiling silently gone; it rejects zero, other negatives and anything past
/// what a timer can count, but only at run time, as a raw
/// <see cref="ArgumentOutOfRangeException"/> naming neither the profile nor the
/// property, and — in <c>CliContainerEngine</c> — after the engine process has
/// already been started.
/// </summary>
internal static class ResourceCeiling
{
    /// <summary>Returns <paramref name="value"/> if it is positive; otherwise throws naming <paramref name="property"/>.</summary>
    internal static long Positive(long value, string property) =>
        value > 0 ? value : throw Rejected(value, property);

    /// <summary>
    /// Returns <paramref name="value"/> if it is a positive, finite number;
    /// otherwise throws naming <paramref name="property"/>. Infinity is excluded on
    /// purpose: it satisfies <c>&gt; 0</c>, so it would pass an ordinary positivity
    /// check and then reach the engine as the literal argument <c>--cpus Infinity</c>,
    /// which docker rejects with a message about parsing rather than about the
    /// profile.
    /// </summary>
    internal static double Positive(double value, string property) =>
        value > 0 && double.IsFinite(value) ? value : throw Rejected(value, property);

    /// <summary>Returns <paramref name="value"/> if it is positive; otherwise throws naming <paramref name="property"/>.</summary>
    internal static int Positive(int value, string property) =>
        value > 0 ? value : throw Rejected(value, property);

    /// <summary>
    /// The longest delay a <see cref="System.Threading.CancellationTokenSource"/>
    /// will count: <c>uint.MaxValue - 1</c> milliseconds, about 49.7 days. Above it
    /// the constructor throws.
    /// </summary>
    internal static readonly TimeSpan MaxEnforceableTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Returns <paramref name="value"/> if it is a duration a timer can actually
    /// enforce — positive, and no longer than <see cref="MaxEnforceableTimeout"/>;
    /// otherwise throws naming <paramref name="property"/>. This is deliberately not
    /// the "would remove the ceiling" diagnosis the other three get: a zero timeout
    /// does not remove anything, it fires at once, and the value that <i>does</i>
    /// remove the ceiling — <c>Timeout.InfiniteTimeSpan</c> — is negative.
    /// </summary>
    internal static TimeSpan Timeout(TimeSpan value, string property) =>
        value > TimeSpan.Zero && value <= MaxEnforceableTimeout
            ? value
            : throw new ArgumentOutOfRangeException(
                property,
                value,
                $"{property} must be a positive duration of at most {MaxEnforceableTimeout}; '{value}' is not a wall-clock ceiling a timer can enforce.");

    private static ArgumentOutOfRangeException Rejected(object value, string property) =>
        new(property, value, $"{property} must be positive; '{value}' would remove the ceiling rather than set one.");
}
