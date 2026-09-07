// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// Tests for <c>Frontmatter</c> semantics. Typed accessors go through a
/// display-string coercion, so scalars other than strings are coerced to
/// their display form and non-scalars yield null.
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
        // Only a sequence value is treated as tags; any other shape
        // (including a bare scalar) yields an empty list. A single scalar
        // `tags` is therefore NOT treated as a one-element list.
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
    public void Extension_keys_exclude_all_known_v02_keys()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse(
                "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ntimestamp: '2026-07-21'\n" +
                "generated: {by: okf4net/0.3.0, at: '2026-07-21'}\nverified: {by: human:ada}\n" +
                "sources: [{resource: r}]\nusage_window: {from: '2026-06-01', to: '2026-06-30'}\n" +
                "status: stable\nstale_after: '2027-01-01'\nextra: 1\n")
                .AsMapping()!);
        Assert.Equal(new[] { "extra" }, fm.ExtensionKeys);
    }

    [Fact]
    public void Required_keys_are_type_title_description()
        => Assert.Equal(new[] { "type", "title", "description" }, Frontmatter.RequiredKeys);

    [Fact]
    public void LastChangedAt_prefers_generated_at_then_falls_back_to_timestamp()
    {
        var withGen = Frontmatter.FromMapping(
            YamlValue.Parse("generated: {by: okf4net/0.3.0, at: '2026-07-27'}\ntimestamp: '2020-01-01'\n").AsMapping()!);
        Assert.Equal("2026-07-27", withGen.LastChangedAt);

        var legacyOnly = Frontmatter.FromMapping(YamlValue.Parse("timestamp: '2020-01-01'\n").AsMapping()!);
        Assert.Equal("2020-01-01", legacyOnly.LastChangedAt);

        Assert.Null(new Frontmatter().LastChangedAt);
    }

    [Fact]
    public void Trust_tier_reads_from_verified()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("verified: {by: human:ada}\n").AsMapping()!);
        Assert.Equal(TrustTier.HumanReviewed, fm.TrustTier);
        Assert.Equal(TrustTier.Unverified, new Frontmatter().TrustTier);
    }

    [Fact]
    public void Lifecycle_and_sources_getters_project_fields()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse("status: deprecated\nstale_after: '2026-01-01'\nsources: [{resource: https://x}]\n").AsMapping()!);
        Assert.Equal(ConceptStatus.Deprecated, fm.Lifecycle.Status);
        Assert.Single(fm.Sources);
        Assert.Equal("https://x", fm.Sources[0].Resource);
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
    public void EffectiveUsageWindow_prefers_the_entrys_own_window_over_the_shared_one()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse(
                "usage_window: {from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z'}\n" +
                "sources:\n  - resource: https://x\n    usage_window: {from: '2026-06-01T00:00:00Z', to: '2026-06-30T00:00:00Z'}\n")
                .AsMapping()!);
        var source = fm.Sources[0];

        var effective = fm.EffectiveUsageWindow(source);

        Assert.Equal(new UsageWindow("2026-06-01T00:00:00Z", "2026-06-30T00:00:00Z"), effective);
    }

    [Fact]
    public void EffectiveUsageWindow_falls_back_to_the_shared_window_when_the_entry_has_none()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse(
                "usage_window: {from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z'}\n" +
                "sources:\n  - resource: https://x\n")
                .AsMapping()!);
        var source = fm.Sources[0];

        var effective = fm.EffectiveUsageWindow(source);

        Assert.Equal(new UsageWindow("2026-01-01T00:00:00Z", "2026-01-31T00:00:00Z"), effective);
    }

    [Fact]
    public void EffectiveUsageWindow_is_null_when_neither_entry_nor_shared_window_exists()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("sources:\n  - resource: https://x\n").AsMapping()!);
        var source = fm.Sources[0];

        Assert.Null(fm.EffectiveUsageWindow(source));
    }

    [Fact]
    public void EffectiveUsageWindow_override_is_whole_object_not_a_per_field_merge()
    {
        // The load-bearing test (decision 1): an entry writing `usage_window: {
        // from: X }` yields a window whose `To` is null. It must NOT inherit
        // the shared window's `to` -- a per-field merge is a rule §5.1 does
        // not state, and would let a half-written entry silently borrow half
        // a window from the shared sibling.
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse(
                "usage_window: {from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z'}\n" +
                "sources:\n  - resource: https://x\n    usage_window: {from: '2026-06-01T00:00:00Z'}\n")
                .AsMapping()!);
        var source = fm.Sources[0];

        var effective = fm.EffectiveUsageWindow(source);

        Assert.NotNull(effective);
        Assert.Equal("2026-06-01T00:00:00Z", effective.Value.From);
        Assert.Null(effective.Value.To); // NOT the shared window's "2026-01-31T00:00:00Z"
    }

    [Fact]
    public void EffectiveUsageWindow_present_and_empty_entry_window_is_not_absent_and_does_not_fall_back()
    {
        // usage_window: {} on an entry parses to new UsageWindow(null, null) --
        // present, just empty -- which is NOT the same as the entry having no
        // usage_window at all. A per-field-merge mutant would treat an empty
        // entry window as "nothing to override with" and silently fall back to
        // the shared bounds; this pins that the whole-object override still
        // wins even when it is empty.
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse(
                "usage_window: {from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z'}\n" +
                "sources:\n  - resource: https://x\n    usage_window: {}\n")
                .AsMapping()!);
        var source = fm.Sources[0];

        var effective = fm.EffectiveUsageWindow(source);

        Assert.NotNull(effective); // present, not absent/null
        Assert.Null(effective.Value.From);
        Assert.Null(effective.Value.To); // NOT the shared window's bounds
    }

    [Fact]
    public void EffectiveUsageWindow_falls_back_to_shared_when_the_entrys_override_is_not_a_mapping()
    {
        // Decision 2: a malformed override (usage_window present but not a
        // mapping) parses to null via ParseUsageWindow's existing leniency,
        // so the entry inherits the shared window exactly as an absent one
        // does.
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse(
                "usage_window: {from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z'}\n" +
                "sources:\n  - resource: https://x\n    usage_window: hello\n")
                .AsMapping()!);
        var source = fm.Sources[0];

        var effective = fm.EffectiveUsageWindow(source);

        Assert.Equal(new UsageWindow("2026-01-01T00:00:00Z", "2026-01-31T00:00:00Z"), effective);
    }

    [Fact]
    public void Equality_is_structural_over_the_underlying_mapping()
    {
        // F11: Frontmatter equality is structural over its underlying
        // mapping (a single-field wrapper whose equality is structural).
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
