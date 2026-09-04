// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// The two filesystem reads the extraction stage performs, behind a seam so a test can observe them
/// and provoke them.
///
/// <para><b>Why this exists rather than direct <c>File.*</c> calls.</b> Two guarantees on this path
/// were stated and could not be executed. That a file's declared length alone decides
/// <see cref="FileStatus.SkippedTooLarge"/> -- proving it needs to observe whether the bytes were
/// read at all, and a status cannot say. And that an unreadable <c>.csproj</c> degrades one file
/// instead of ending the repository's run -- provoking it needs an <see cref="System.UnauthorizedAccessException"/>
/// from a read, which on this platform needs an ACL and an elevation a test suite does not have.
/// Both are answerable once the read is a collaborator.</para>
///
/// <para>Deliberately two members and no more. This is not a filesystem abstraction: writes,
/// enumeration and metadata stay on <see cref="System.IO.File"/> and <see cref="System.IO.Directory"/>
/// where their callers already are. It covers exactly the reads whose ORDER or FAILURE a test needs
/// to pin.</para>
/// </summary>
public interface IFileSystemReader
{
    /// <summary>
    /// The file's length in bytes, or <see langword="null"/> when it does not exist or is not a file.
    /// Answers for the instant it is asked and nothing later -- a caller that then opens the file
    /// still handles failure.
    /// </summary>
    long? TryGetLength(string absolutePath);

    /// <summary>
    /// Opens the file for reading. Throws exactly what <see cref="System.IO.File.OpenRead"/> throws;
    /// callers on this path treat a failure as a skipped file, never as a failed run.
    /// </summary>
    Stream OpenRead(string absolutePath);
}

/// <summary>
/// <see cref="IFileSystemReader"/> over the real filesystem -- the default everywhere in production,
/// and the reason every seam parameter can carry a default rather than ripple through call sites.
/// </summary>
public sealed class SystemFileReader : IFileSystemReader
{
    /// <summary>The single shared instance; the type is stateless.</summary>
    public static SystemFileReader Instance { get; } = new();

    /// <inheritdoc/>
    public long? TryGetLength(string absolutePath)
    {
        var info = new FileInfo(absolutePath);
        return info.Exists ? info.Length : null;
    }

    /// <inheritdoc/>
    public Stream OpenRead(string absolutePath) => File.OpenRead(absolutePath);
}
