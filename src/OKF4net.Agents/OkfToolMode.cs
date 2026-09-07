// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Agents;

/// <summary>
/// How <see cref="OkfBundleTools.GetTools(OkfToolMode)"/> exposes the three
/// write-capable tools (<see cref="OkfBundleTools.WriteToolNames"/>).
///
/// <para>
/// The distinction matters because bundle content is untrusted: concept bodies,
/// frontmatter and log entries come from files that another agent or a human
/// contributor may have written. A prompt injection carried in one of them can
/// only cause a persistent change if a write tool is reachable without a human
/// in the loop.
/// </para>
/// </summary>
public enum OkfToolMode
{
    /// <summary>
    /// Every tool, with the write tools ungated — the historical behaviour of
    /// the parameterless <see cref="OkfBundleTools.GetTools()"/>, and still its
    /// meaning so existing hosts are not silently changed.
    ///
    /// <para>
    /// Ungated does NOT mean the Agent Framework will ask on your behalf: a
    /// plain <c>AIFunction</c> is invoked directly. A host that wants a
    /// confirmation step must choose <see cref="RequireApprovalForWrites"/> or
    /// wrap the tools itself.
    /// </para>
    /// </summary>
    ReadWrite,

    /// <summary>
    /// The write tools are omitted entirely. For a host that must never mutate
    /// the bundle — a shared or pinned corpus, a demo, a read-only MCP server.
    /// Nothing else is removed.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Every tool, with the write tools wrapped in
    /// <c>ApprovalRequiredAIFunction</c> so the Agent Framework surfaces an
    /// approval request before invoking one.
    ///
    /// <para>
    /// Read tools are deliberately left unwrapped. Gating everything trains a
    /// user to click through prompts, which is precisely how the one approval
    /// that mattered gets waved past.
    /// </para>
    /// </summary>
    RequireApprovalForWrites,
}
