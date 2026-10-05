// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Tests.TestSupport;

/// <summary>
/// The one way this suite removes a scratch directory tree it built, replacing a per-fixture
/// <c>Directory.Delete(path, recursive: true)</c> inside <c>catch (IOException) { }</c>.
///
/// <para>
/// A bare recursive delete leaves a tree behind on Windows in two situations this suite creates on
/// every run -- measured, not assumed (#119): (1) a directory junction (<c>mklink /J</c>, what
/// <see cref="DirectoryLinks"/> falls back to) makes the recursive delete throw
/// <see cref="UnauthorizedAccessException"/> on the link itself, after it has already removed the link's
/// target, so the swallowed exception leaves a dangling junction and a directory that looks empty; (2)
/// <c>git</c> writes its object files read-only, so a fixture repository's <c>.git/objects</c> cannot
/// be deleted until the attribute is cleared. Both were swallowed by the old disposers, which is why
/// the leak was invisible: only the directory count in the system temp grew.
/// </para>
///
/// <para>
/// So the delete removes links first, without ever descending into one (a link's target may be outside
/// the tree and must not be touched), clears read-only attributes, and only then deletes recursively.
/// It stays best-effort: a tree that still cannot be removed (a handle held by another process) is left
/// and reported to the caller as <see langword="false"/> rather than failing a test run on the way out.
/// </para>
/// </summary>
internal static class TempTree
{
    /// <summary>Removes <paramref name="path"/> and everything under it; returns whether it is gone.</summary>
    public static bool Delete(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return true;
        }

        try
        {
            RemoveLinksAndClearReadOnly(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort: never fail a run over scratch cleanup; the return value says what happened.
        }

        return !Directory.Exists(path);
    }

    private static void RemoveLinksAndClearReadOnly(DirectoryInfo directory)
    {
        // AttributesToSkip = 0: the default skips Hidden and System entries, which would leave a
        // read-only or linked entry behind precisely because it carries one of those attributes.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };

        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A directory link is removed as the link itself (never recursively, never through it);
                // a file link is an ordinary File.Delete.
                if (entry is DirectoryInfo)
                {
                    Directory.Delete(entry.FullName, recursive: false);
                }
                else
                {
                    File.Delete(entry.FullName);
                }

                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (entry is DirectoryInfo child)
            {
                RemoveLinksAndClearReadOnly(child);
            }
        }
    }
}
