// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Validation;

/// <summary>Thin wrapper around <c>OKF4net.BundleValidator</c> for the <c>validate</c> command.</summary>
public interface IBundleValidationRunner
{
    /// <summary>
    /// Loads and validates the bundle at <paramref name="bundleRoot"/> against the real wall clock
    /// (<see cref="SystemClock"/>), which is what an operator running <c>validate</c> wants: a concept
    /// is stale (§5.5) relative to today.
    /// </summary>
    /// <exception cref="OKF4net.BundleLoadException"><paramref name="bundleRoot"/> does not exist or is not a directory.</exception>
    ValidationOutcome Validate(string bundleRoot);

    /// <summary>
    /// Loads and validates the bundle at <paramref name="bundleRoot"/> against
    /// <paramref name="clock"/>.
    ///
    /// <para>An overload rather than an optional parameter, so the existing call sites keep working
    /// untouched. It exists because the one thing a validation result must not depend on is the day it
    /// ran: <c>BundleValidator</c> reads "today" from an <see cref="IOkfClock"/> for its staleness check,
    /// and a caller that cannot supply one -- a test asserting that a generated bundle is conformant,
    /// above all -- silently gets <see cref="SystemClock"/> and a result that can change overnight with
    /// no change to the bundle or to this producer.</para>
    /// </summary>
    /// <param name="bundleRoot">Root directory of the bundle to validate.</param>
    /// <param name="clock">Supplies "today" for §5.5's staleness check; <see cref="FixedClock"/> pins it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    /// <exception cref="OKF4net.BundleLoadException"><paramref name="bundleRoot"/> does not exist or is not a directory.</exception>
    ValidationOutcome Validate(string bundleRoot, IOkfClock clock);
}
