// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="MemoryPath.For"/> encoding contract: fixed literal tier
/// prefixes, a readable lowercased-raw prefix plus a hash suffix per real
/// segment, the bare <see cref="MemoryPath.LocalSentinel"/> for null segments,
/// and — the security crux — case-variant segments mapping to
/// case-insensitively distinct paths. Assertions derive from behaviour /
/// structure, never a hardcoded hash length.
/// </summary>
public class MemoryPathTests
{
    // Splits an encoded "<readable>-<hash>" segment at the LAST '-' (the hash
    // is dash-free lowercase hex, so the last '-' is always the separator) and
    // asserts the readable prefix and a non-empty lowercase-hex suffix, without
    // pinning the suffix length.
    private static void AssertEncoded(string segment, string expectedReadable)
    {
        var dash = segment.LastIndexOf('-');
        Assert.True(dash > 0, $"segment '{segment}' has no hash suffix");
        Assert.Equal(expectedReadable, segment[..dash]);

        var hash = segment[(dash + 1)..];
        Assert.NotEmpty(hash);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c), $"hash char '{c}' is not lowercase hex"));
    }

    [Fact]
    public void Tenant_tier_keeps_its_literal_prefix_with_an_encoded_segment()
    {
        var segments = MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "acme")).Split('/');
        Assert.Equal(2, segments.Length);
        Assert.Equal("memory-tenant", segments[0]);
        AssertEncoded(segments[1], "acme");
    }

    [Fact]
    public void User_tier_nests_an_encoded_user_segment_under_the_encoded_tenant_segment()
    {
        var segments = MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(tenantId: "acme", userId: "alice")).Split('/');
        Assert.Equal(3, segments.Length);
        Assert.Equal("memory-user", segments[0]);
        AssertEncoded(segments[1], "acme");
        AssertEncoded(segments[2], "alice");
    }

    [Fact]
    public void Session_tier_nests_an_encoded_session_segment_under_tenant_and_user()
    {
        var segments = MemoryPath.For(MemoryTier.Session, new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "s1")).Split('/');
        Assert.Equal(4, segments.Length);
        Assert.Equal("memory-session", segments[0]);
        AssertEncoded(segments[1], "acme");
        AssertEncoded(segments[2], "alice");
        AssertEncoded(segments[3], "s1");
    }

    [Fact]
    public void The_readable_prefix_is_lowercased()
    {
        var segments = MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "ACME")).Split('/');
        AssertEncoded(segments[1], "acme");
    }

    [Fact]
    public void Null_tenant_renders_the_bare_local_sentinel_and_user_is_encoded_under_it()
    {
        var segments = MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(userId: "alice")).Split('/');
        Assert.Equal("memory-user", segments[0]);
        Assert.Equal(MemoryPath.LocalSentinel, segments[1]); // bare, no hash
        AssertEncoded(segments[2], "alice");

        Assert.Equal($"memory-tenant/{MemoryPath.LocalSentinel}", MemoryPath.For(MemoryTier.Tenant, KnowledgeAccessScope.Local));
    }

    [Fact]
    public void Fully_local_scope_is_all_bare_sentinels_for_every_tier()
    {
        var local = KnowledgeAccessScope.Local;
        Assert.Equal("memory-user/_local/_local", MemoryPath.For(MemoryTier.User, local));
        Assert.Equal("memory-session/_local/_local/_local", MemoryPath.For(MemoryTier.Session, local));
        Assert.Equal("memory-tenant/_local", MemoryPath.For(MemoryTier.Tenant, local));
    }

    [Fact]
    public void Case_variant_segments_map_to_case_insensitively_distinct_paths()
    {
        var upper = MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(tenantId: "Acme", userId: "alice"));
        var lower = MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(tenantId: "acme", userId: "alice"));

        // The whole point on a case-insensitive filesystem: the readable
        // prefixes collide ("acme"), but the paths stay distinct even under a
        // case-insensitive comparison because the hash is of the case-SENSITIVE
        // raw bytes.
        Assert.NotEqual(upper, lower);
        Assert.False(string.Equals(upper, lower, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("memory-user/acme-", upper, StringComparison.Ordinal);
        Assert.StartsWith("memory-user/acme-", lower, StringComparison.Ordinal);
    }

    [Fact]
    public void Encoding_is_deterministic_for_the_same_input()
    {
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        Assert.Equal(MemoryPath.For(MemoryTier.User, scope), MemoryPath.For(MemoryTier.User, scope));
    }

    [Fact]
    public void An_encoded_real_segment_never_collides_with_the_local_sentinel()
    {
        // Even a real tenant whose readable form resembles the sentinel carries
        // a hash suffix, so it can never equal the bare "_local" segment.
        var segment = MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "local")).Split('/')[1];
        Assert.NotEqual(MemoryPath.LocalSentinel, segment);
        AssertEncoded(segment, "local");
    }
}
