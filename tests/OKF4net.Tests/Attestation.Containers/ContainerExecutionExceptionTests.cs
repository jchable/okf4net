// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ContainerExecutionExceptionTests
{
    /// <summary>
    /// A host logs an exception through <c>ToString()</c> — every logger does. Without the
    /// container's stderr there, a failed run reads as a bare "exited with code 1" and the
    /// actual cause (here, Python finding no writable temporary directory) is lost.
    /// </summary>
    [Fact]
    public void ToString_carries_the_containers_stderr()
    {
        var ex = new ContainerExecutionException("attester exited with code 1", "", "FileNotFoundError: No usable temporary directory found");

        Assert.Contains("attester exited with code 1", ex.ToString());
        Assert.Contains("No usable temporary directory found", ex.ToString());
    }

    [Fact]
    public void ToString_carries_the_containers_stdout_when_there_is_some()
    {
        var ex = new ContainerExecutionException("executor stdout was not valid JSON", "not json at all", "");

        Assert.Contains("not json at all", ex.ToString());
    }

    /// <summary>
    /// The message is what callers match on and what ends up in one-line summaries; it
    /// stays exactly what the throw site wrote.
    /// </summary>
    [Fact]
    public void Message_is_left_unchanged()
    {
        var ex = new ContainerExecutionException("attester exited with code 1", "out", "err");

        Assert.Equal("attester exited with code 1", ex.Message);
    }

    /// <summary>
    /// Captured output can be megabytes. A log line must stay readable, and the end of the
    /// stream is where a traceback puts the actual error, so the tail is what is kept.
    /// </summary>
    [Fact]
    public void ToString_keeps_only_a_bounded_tail_of_a_large_stream()
    {
        var stderr = "HEAD-MARKER" + new string('x', 1_000_000) + "TAIL-MARKER";
        var ex = new ContainerExecutionException("boom", "", stderr);

        var text = ex.ToString();

        Assert.Contains("TAIL-MARKER", text);
        Assert.DoesNotContain("HEAD-MARKER", text);
        Assert.True(text.Length < 16_000, $"ToString() was {text.Length} characters");
    }

    [Fact]
    public void ToString_names_no_stream_that_was_empty()
    {
        var ex = new ContainerExecutionException("concept has no resolvable attester.resource", "", "");

        Assert.DoesNotContain("stderr", ex.ToString());
        Assert.DoesNotContain("stdout", ex.ToString());
    }
}
