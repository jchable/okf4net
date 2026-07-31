// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>One provenance source entry (§5.1). <see cref="Resource"/> is required within the entry; a missing one is surfaced as "" for the validator to flag.</summary>
public readonly record struct Source(string? Id, string Resource, string? Title, Actor? Author, long? UsageCount, string? LastModified);

/// <summary>The <c>usage_window</c> that frames every <c>usage_count</c> (§5.1); a top-level sibling of <c>sources</c>.</summary>
public readonly record struct UsageWindow(string? From, string? To);

/// <summary>Parses the §5.1 provenance frontmatter. Lenient: non-conforming shapes yield empty/default rather than throwing.</summary>
public static class Provenance
{
    /// <summary>Parses the <c>sources</c> sequence; a non-sequence value yields an empty list.</summary>
    public static IReadOnlyList<Source> ParseSources(YamlValue? value)
    {
        if (value is not YamlSequence seq)
        {
            return [];
        }

        var list = new List<Source>();
        foreach (var item in seq.Items)
        {
            if (item is not YamlMapping m)
            {
                continue;
            }

            var author = m.Get("author")?.AsDisplayString();
            list.Add(new Source(
                Id: m.Get("id")?.AsDisplayString(),
                Resource: m.Get("resource")?.AsDisplayString() ?? "",
                Title: m.Get("title")?.AsDisplayString(),
                Author: author is null ? null : Actor.Parse(author),
                UsageCount: m.Get("usage_count")?.AsInt(),
                LastModified: m.Get("last_modified")?.AsDisplayString()));
        }

        return list;
    }

    /// <summary>Parses the <c>usage_window</c> mapping; a non-mapping value yields null.</summary>
    public static UsageWindow? ParseUsageWindow(YamlValue? value)
        => value is YamlMapping m ? new UsageWindow(m.Get("from")?.AsDisplayString(), m.Get("to")?.AsDisplayString()) : null;

    /// <summary>
    /// Serializes §5.1 provenance sources to the <see cref="YamlSequence"/> <see cref="ParseSources"/>
    /// reads back. Each entry uses the canonical key order <c>id, resource, title, author,
    /// usage_count, last_modified</c> (the order <see cref="ParseSources"/> itself reads them in); a
    /// <see langword="null"/> field on a <see cref="Source"/> is omitted from its mapping rather than
    /// written as an explicit YAML null. <paramref name="sources"/> is enumerated exactly once, and
    /// the order of its elements is preserved unchanged in the returned sequence (no sorting, no
    /// deduplication).
    /// </summary>
    public static YamlSequence ToYaml(IEnumerable<Source> sources)
    {
        var items = new List<YamlValue>();
        foreach (var source in sources)
        {
            var map = new YamlMapping();
            if (source.Id is not null)
            {
                map.Insert("id", new YamlString(source.Id));
            }

            map.Insert("resource", new YamlString(source.Resource));

            if (source.Title is not null)
            {
                map.Insert("title", new YamlString(source.Title));
            }

            if (source.Author is not null)
            {
                map.Insert("author", new YamlString(source.Author.Value.Raw));
            }

            if (source.UsageCount is not null)
            {
                map.Insert("usage_count", new YamlInt(source.UsageCount.Value));
            }

            if (source.LastModified is not null)
            {
                map.Insert("last_modified", new YamlString(source.LastModified));
            }

            items.Add(map);
        }

        return new YamlSequence(items);
    }
}
