// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Mcp;

/// <summary>
/// Convention-based discovery of an OKF bundle root: starting from a
/// directory and walking up to the filesystem root, the first candidate that
/// is a <em>marked</em> bundle wins. At each level the directory itself is
/// tested before its <c>knowledge/</c> child. A directory is a marked bundle
/// when its root <c>index.md</c> frontmatter declares <c>okf_version</c>
/// (§11) — the only zero-false-positive marker available, so unmarked
/// bundles are deliberately not discovered: a writable server must never
/// mistake an arbitrary docs directory for a bundle. The escape hatches are
/// the positional argument and <c>OKF_BUNDLE_ROOT</c>.
///
/// Symlink stance: the walk is purely lexical
/// (<see cref="Path.GetFullPath(string)"/> / <see cref="Path.GetDirectoryName(string)"/>
/// resolve no links, so there is no cycle risk), and reading a candidate's
/// <c>index.md</c> through a link mirrors <see cref="Bundle.OkfVersion"/>'s
/// existing stance. The library's reparse-point guards apply where they
/// always did — when the chosen root is actually loaded and served.
/// </summary>
public static class OkfBundleDiscovery
{
    /// <summary>Root index filename probed in each candidate directory.</summary>
    public const string IndexFilename = "index.md";

    /// <summary>Conventional child directory name probed at each level.</summary>
    public const string ConventionChildName = "knowledge";

    // Same strict UTF-8 as the library's internal OkfEncodings.Strict (no BOM,
    // throw on invalid bytes); reconstructed here because OKF4net.Mcp has no
    // InternalsVisibleTo grant and one constructor call does not warrant one.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Walks from <paramref name="startDirectory"/> up to the filesystem root
    /// looking for a marked bundle. Pure given
    /// <paramref name="readRootIndex"/> (candidate directory → its root
    /// <c>index.md</c> text, or <see langword="null"/> when absent or
    /// unreadable), so walk order and precedence are unit-testable without a
    /// filesystem; pass <see cref="ReadRootIndexOrNull"/> in production.
    /// </summary>
    /// <param name="startDirectory">Directory the walk starts from (made absolute first); an empty or invalid path yields <see langword="false"/>, never a throw.</param>
    /// <param name="readRootIndex">Candidate directory → root index text, or null.</param>
    /// <param name="bundleRoot">The discovered bundle root (empty when not found).</param>
    /// <returns><see langword="true"/> when a marked bundle was found.</returns>
    public static bool TryDiscover(string startDirectory, Func<string, string?> readRootIndex, out string bundleRoot)
    {
        bundleRoot = string.Empty;

        string? dir;
        try
        {
            dir = Path.GetFullPath(startDirectory);
        }
        catch (ArgumentException)
        {
            // Try-contract: an empty or malformed start path is "not found",
            // not an exception escaping a Try* method.
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }

        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var candidate in new[] { dir, Path.Combine(dir, ConventionChildName) })
            {
                var text = readRootIndex(candidate);
                if (text is not null && DeclaresOkfVersion(text))
                {
                    bundleRoot = candidate;
                    return true;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    /// <summary>
    /// Production <c>readRootIndex</c> accessor: reads
    /// <c>&lt;directory&gt;/index.md</c> as strict UTF-8, returning
    /// <see langword="null"/> on any read failure (missing file, I/O error,
    /// permission denied, invalid UTF-8) — the same "unreadable means no
    /// declared version" stance as <see cref="Bundle.OkfVersion"/>.
    /// </summary>
    /// <param name="directory">Candidate bundle root.</param>
    /// <returns>The root index text, or <see langword="null"/>.</returns>
    public static string? ReadRootIndexOrNull(string directory)
    {
        try
        {
            return StrictUtf8.GetString(File.ReadAllBytes(Path.Combine(directory, IndexFilename)));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool DeclaresOkfVersion(string indexText) =>
        OkfDocument.TryParse(indexText, out var doc, out _)
        && doc.Frontmatter.Get("okf_version") is not null;
}
