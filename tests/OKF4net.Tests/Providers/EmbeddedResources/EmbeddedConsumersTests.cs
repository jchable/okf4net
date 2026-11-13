// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>Spec §4.4, §6.1: every consumer sees a read-only provider; nothing reaches the disk.</summary>
#pragma warning disable CS0618 // OkfBundleTools' string methods are obsolete bridges, still public entry points
[Collection(CurrentDirectoryCollection.Name)]
public class EmbeddedConsumersTests
{
    private const string Fm = "type: Note\ntitle: New\ndescription: A new note.";
    private static EmbeddedResourceBundleProvider Contract() => new(typeof(EmbeddedConsumersTests).Assembly, "contract");

    [Fact]
    public void The_writer_and_the_index_generator_refuse_before_attempting()
    {
        var writer = new BundleConceptWriter(Contract());
        Assert.Throws<BundleReadOnlyException>(() => writer.WriteConcept("notes/c", Fm, "C\n"));
        Assert.Throws<BundleReadOnlyException>(() => writer.AppendLog("Update", "x"));
        Assert.Throws<BundleReadOnlyException>(() => IndexGenerator.RegenerateIndexes(Contract()));
    }

    [Fact]
    public void The_tool_set_offers_no_write_tool_and_its_writes_refuse()
    {
        using var handle = new BundleHandle(Contract());
        var tools = new OkfBundleTools(handle);
        var names = tools.GetTools(OkfToolMode.ReadWrite).Select(t => t.Name).ToArray();
        Assert.Contains("okf_read_concept", names);
        Assert.DoesNotContain("okf_write_concept", names);
        Assert.Throws<BundleReadOnlyException>(() => tools.WriteConcept("notes/c", Fm, "C\n"));
    }

    [Fact]
    public void Every_read_entry_point_leaves_an_empty_current_directory_empty()
    {
        using var empty = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(empty.Path);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
            var bundle = Bundle.Load(Contract());
            _ = BundleValidator.Validate(bundle, clock);
            _ = ConceptAudit.Run(bundle, default, clock);
            Assert.NotEmpty(ConceptSearch.Search(bundle.Concepts, "note"));
            Assert.True(bundle.TryResolveResourceKey(bundle.Concepts.First(c => c.Id.ToString() == "notes/a"), "/refs/q.sql", out var key, out _));
            Assert.Equal("SELECT 1\n", bundle.ReadResourceText(key!).Replace("\r\n", "\n"));

            using var handle = new BundleHandle(Contract());
            var tools = new OkfBundleTools(handle);
            Assert.Contains("Links to", tools.ReadConcept("notes/a"), StringComparison.Ordinal);
            Assert.Contains("notes/a", tools.Search("first"), StringComparison.Ordinal);
            Assert.StartsWith("# contract", tools.Browse(), StringComparison.Ordinal); // the root index.md, served as is
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(empty.Path));
    }
}
#pragma warning restore CS0618
