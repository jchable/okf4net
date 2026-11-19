// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Test-only: the result of a task that completes without waiting for anything outside the
/// process (the tool set's own lock over a provider that completes synchronously, #152), for the
/// many tests that read a tool's text. It lives here, not in the test methods, so that the
/// blocking-call analyzer sees one place and the tests stay about the tools.
/// </summary>
internal static class TestTaskExtensions
{
    internal static T Sync<T>(this Task<T> task) => task.GetAwaiter().GetResult();
}
