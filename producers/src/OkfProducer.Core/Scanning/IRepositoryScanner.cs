// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Scanning;

/// <summary>Scans a repository directory and reports what it found.</summary>
public interface IRepositoryScanner
{
    /// <summary>Scans <paramref name="repoPath"/> for packages and documentation.</summary>
    RepositorySnapshot Scan(string repoPath);
}
