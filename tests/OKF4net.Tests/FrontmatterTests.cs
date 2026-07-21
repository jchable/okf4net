// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// Port of the Rust <c>Frontmatter</c> semantics (src/frontmatter.rs). Typed
/// accessors go through <c>as_display_string</c>, so scalars other than
/// strings are coerced to their display form and non-scalars yield null.
/// </summary>
public class FrontmatterTests
{
    [Fact]
    public void New_frontmatter_is_empty()
    {
        var fm = new Frontmatter();
        Assert.True(fm.IsEmpty);
        Assert.True(fm.AsMapping().IsEmpty);
    }

    [Fact]
    public void Typed_getters_read_display_strings()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("type: Metric\ntitle: DAU\ncount: 42\n").AsMapping()!);
        Assert.Equal("Metric", fm.Type);
        Assert.Equal("DAU", fm.Title);
        Assert.Null(fm.Description);
        Assert.Null(fm.Resource);
        Assert.Null(fm.Timestamp);
        Assert.False(fm.IsEmpty);
    }

    [Fact]
    public void Typed_getters_coerce_bool_int_float_scalars_to_display_strings()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse("type: 42\ntitle: true\ndescription: 3.5\n").AsMapping()!);
        Assert.Equal("42", fm.Type);
        Assert.Equal("true", fm.Title);
        Assert.Equal("3.5", fm.Description);
    }

    [Fact]
    public void Typed_getter_is_null_when_value_is_not_a_scalar()
    {
        // A mapping or sequence value has no as_display_string form.
        var fm = Frontmatter.FromMapping(YamlValue.Parse("type: [a, b]\n").AsMapping()!);
        Assert.Null(fm.Type);
    }

    [Fact]
    public void Tags_reads_sequence_items_as_display_strings()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("tags: [a, b]\n").AsMapping()!);
        Assert.Equal(new[] { "a", "b" }, fm.Tags);
    }

    [Fact]
    public void Tags_non_scalar_sequence_items_are_skipped()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("tags:\n  - a\n  - [nested]\n  - b\n").AsMapping()!);
        Assert.Equal(new[] { "a", "b" }, fm.Tags);
    }

    [Fact]
    public void Tags_is_empty_when_value_is_a_single_scalar_not_a_sequence()
    {
        // frontmatter.rs:96-101: only the Value::Sequence arm is handled;
        // any other shape (including a bare scalar) falls through to the
        // wildcard arm and yields an empty Vec. A single scalar `tags` is
        // therefore NOT treated as a one-element list.
        var fm = Frontmatter.FromMapping(YamlValue.Parse("tags: solo\n").AsMapping()!);
        Assert.Empty(fm.Tags);
    }

    [Fact]
    public void Tags_is_empty_when_absent()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("type: T\n").AsMapping()!);
        Assert.Empty(fm.Tags);
    }

    [Fact]
    public void Extension_keys_are_unknown_keys_in_order()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse("type: T\ncustom_z: 1\ntitle: X\ncustom_a: 2\n").AsMapping()!);
        Assert.Equal(new[] { "custom_z", "custom_a" }, fm.ExtensionKeys);
    }

    [Fact]
    public void Extension_keys_excludes_exactly_the_six_known_keys()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse(
                "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ntimestamp: '2026-07-21'\nextra: 1\n")
                .AsMapping()!);
        Assert.Equal(new[] { "extra" }, fm.ExtensionKeys);
    }

    [Fact]
    public void Get_returns_raw_value_for_arbitrary_key()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("custom: 7\n").AsMapping()!);
        Assert.Equal(7L, fm.Get("custom")!.AsInt());
        Assert.Null(fm.Get("missing"));
    }

    [Fact]
    public void Set_writes_through_to_underlying_mapping_preserving_position()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("a: 1\nb: 2\n").AsMapping()!);
        fm.Set("a", new YamlInt(99));
        Assert.Equal(new[] { "a", "b" }, fm.AsMapping().Keys.ToArray());
        Assert.Equal(99L, fm.AsMapping().Get("a")!.AsInt());
        Assert.Equal(99L, fm.Get("a")!.AsInt());
    }

    [Fact]
    public void Set_appends_new_key_at_end()
    {
        var fm = new Frontmatter();
        fm.Set("type", new YamlString("Metric"));
        fm.Set("title", new YamlString("DAU"));
        Assert.Equal(new[] { "type", "title" }, fm.AsMapping().Keys.ToArray());
        Assert.Equal("Metric", fm.Type);
    }

    [Fact]
    public void AsMapping_exposes_the_full_ordered_mapping()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("type: T\ncustom: 1\n").AsMapping()!);
        Assert.Equal(new[] { "type", "custom" }, fm.AsMapping().Keys.ToArray());
    }

    [Fact]
    public void Equality_is_structural_over_the_underlying_mapping()
    {
        // F11: Rust derives PartialEq on Frontmatter (frontmatter.rs:18), a
        // single-field wrapper over Mapping, whose PartialEq is structural.
        var a = Frontmatter.FromMapping(YamlValue.Parse("type: T\ncustom: 1\n").AsMapping()!);
        var b = Frontmatter.FromMapping(YamlValue.Parse("type: T\ncustom: 1\n").AsMapping()!);
        Assert.Equal(a, b);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var different = Frontmatter.FromMapping(YamlValue.Parse("type: T\ncustom: 2\n").AsMapping()!);
        Assert.NotEqual(a, different);
        Assert.False(a.Equals(different));
        Assert.False(a.Equals(null));
    }
}
