namespace OKF4net.Tests;

/// <summary>
/// Shared test helper: a tiny dependency-free temporary-directory fixture.
/// Port of <c>tests/common/mod.rs</c>. Only the subset of members exercised
/// by the ported tests is included (<see cref="Path"/>, <see cref="Write"/>,
/// <see cref="Dispose"/>) — Rust's <c>read</c>/<c>mkdir</c> helpers are
/// unused by the Task 8 tests and are omitted.
/// </summary>
public sealed class TempDir : IDisposable
{
    /// <summary>The directory path.</summary>
    public string Path { get; }

    /// <summary>Creates a fresh unique temporary directory.</summary>
    public TempDir()
    {
        Path = Directory.CreateTempSubdirectory("okf4net-").FullName;
    }

    /// <summary>Writes a file (creating parent directories) relative to the temp root, as UTF-8 without a BOM.</summary>
    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
        return full;
    }

    /// <summary>Removes the temporary directory and its contents (best-effort).</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup, mirroring Rust's `Drop` impl (tests/common/mod.rs:57-61).
        }
    }
}
