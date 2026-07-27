// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

public class KnowledgeAccessScopeTests
{
    [Fact]
    public void All_null_is_local()
    {
        var scope = new KnowledgeAccessScope();
        Assert.True(scope.IsLocal);
        Assert.True(KnowledgeAccessScope.Local.IsLocal);
    }

    [Fact]
    public void Non_null_segments_are_kept()
    {
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "s1");
        Assert.False(scope.IsLocal);
        Assert.Equal("acme", scope.TenantId);
        Assert.Equal("alice", scope.UserId);
        Assert.Equal("s1", scope.SessionId);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("has space")]
    [InlineData("")]
    public void Invalid_segment_is_rejected(string bad)
    {
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(tenantId: bad));
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(userId: bad));
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(sessionId: bad));
    }

    [Fact]
    public void Reserved_local_sentinel_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(tenantId: MemoryPath.LocalSentinel));
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(userId: MemoryPath.LocalSentinel));
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(sessionId: MemoryPath.LocalSentinel));
    }
}
