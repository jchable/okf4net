// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>
/// Fluent, in-memory builder for an <see cref="OkfDocument"/> — for a programmatic caller (e.g. a
/// producer) that constructs a concept entirely in memory, as an alternative to hand-writing YAML
/// frontmatter text. Does not validate; call <see cref="OkfDocument.Validate"/> or
/// <see cref="OkfDocument.ValidateConformance"/> on the built document explicitly.
/// </summary>
public sealed class OkfDocumentBuilder
{
    private readonly string _type;
    private string? _title;
    private string? _description;
    private string? _resource;
    private readonly List<string> _tags = [];
    private readonly List<Source> _sources = [];
    private readonly YamlMapping _extensions = new();
    private string? _body;

    private OkfDocumentBuilder(string type) => _type = type;

    /// <summary>Starts a new builder for a concept of the given <c>type</c> — §4.1's one required field.</summary>
    public static OkfDocumentBuilder ForType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new OkfDocumentBuilder(type);
    }

    /// <summary>Sets (overwriting any previous value) the <c>title</c> field.</summary>
    public OkfDocumentBuilder Title(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        _title = title;
        return this;
    }

    /// <summary>Sets (overwriting any previous value) the <c>description</c> field.</summary>
    public OkfDocumentBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _description = description;
        return this;
    }

    /// <summary>Sets (overwriting any previous value) the <c>resource</c> field.</summary>
    public OkfDocumentBuilder Resource(string resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _resource = resource;
        return this;
    }

    /// <summary>
    /// Replaces the entire accumulated tag list — including anything a prior <see cref="AddTags"/>
    /// call added — with <paramref name="tags"/>, in the given order. This happens regardless of
    /// whether the <see cref="AddTags"/> call came before or after this one in the fluent chain.
    /// Call <see cref="AddTags"/> instead to accumulate rather than replace.
    /// </summary>
    public OkfDocumentBuilder Tags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags.Clear();
        _tags.AddRange(tags);
        return this;
    }

    /// <summary>
    /// Appends to the accumulated tag list, in call order. Call <see cref="Tags"/> instead to
    /// replace the whole list.
    /// </summary>
    public OkfDocumentBuilder AddTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags.AddRange(tags);
        return this;
    }

    /// <summary>
    /// Appends one §5.1 provenance source, in call order. Does not validate <paramref name="resource"/>
    /// (producer-grade validation stays in <see cref="BundleValidator"/>/<see cref="OkfDocument.Validate"/>).
    /// </summary>
    public OkfDocumentBuilder AddSource(
        string resource,
        string? id = null,
        string? title = null,
        Actor? author = null,
        long? usageCount = null,
        string? lastModified = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _sources.Add(new Source(id, resource, title, author, usageCount, lastModified));
        return this;
    }

    /// <summary>
    /// Sets an arbitrary frontmatter key — a producer-defined extension key, or (with no collision
    /// guard) one of the well-known keys also covered by a typed method above. See <see cref="Build"/>'s
    /// remarks for the resulting key order and what a collision resolves to.
    /// </summary>
    public OkfDocumentBuilder Extension(string key, YamlValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _extensions.Insert(key, value);
        return this;
    }

    /// <summary>Sets (overwriting any previous value) the document body. Mandatory before <see cref="Build"/>.</summary>
    public OkfDocumentBuilder Body(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
        return this;
    }

    /// <summary>
    /// Builds an <see cref="OkfDocument"/> from the accumulated state. Idempotent and non-destructive:
    /// may be called more than once on the same builder; each call returns a fresh document
    /// reflecting the builder's current state at that moment. Does not validate.
    ///
    /// Key order is fixed, not call order: <c>type, title, description, resource, tags, sources</c>
    /// (the subset of <see cref="Frontmatter.KnownKeys"/>'s own order this builder covers — only
    /// present when the corresponding field was set, except <c>type</c> which is always present),
    /// followed by any <see cref="Extension"/> keys in their own call order. Because
    /// <see cref="Extension"/> is always applied after the six well-known keys, an
    /// <see cref="Extension"/> call targeting one of them (e.g. <c>Extension("tags", ...)</c>) always
    /// wins over the corresponding typed setter's value, regardless of the two calls' order in the
    /// fluent chain — a deliberate simplification (fixed application order, not call-order tracking),
    /// not a bug.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Body"/> was never called.</exception>
    public OkfDocument Build()
    {
        if (_body is null)
        {
            throw new InvalidOperationException("OkfDocumentBuilder: Body(...) must be called before Build().");
        }

        var map = new YamlMapping();
        map.Insert("type", new YamlString(_type));

        if (_title is not null)
        {
            map.Insert("title", new YamlString(_title));
        }

        if (_description is not null)
        {
            map.Insert("description", new YamlString(_description));
        }

        if (_resource is not null)
        {
            map.Insert("resource", new YamlString(_resource));
        }

        if (_tags.Count > 0)
        {
            if (_tags.Any(t => t is null))
            {
                throw new ArgumentException("OkfDocumentBuilder: tags must not contain a null element (set via Tags(...) or AddTags(...)).");
            }

            map.Insert("tags", new YamlSequence(_tags.Select(t => (YamlValue)new YamlString(t)).ToList()));
        }

        if (_sources.Count > 0)
        {
            map.Insert("sources", Provenance.ToYaml(_sources));
        }

        foreach (var (key, value) in _extensions.Entries)
        {
            map.Insert(key.AsString()!, value);
        }

        return new OkfDocument(Frontmatter.FromMapping(map), _body);
    }
}
