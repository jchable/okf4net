// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

public class MemoryPathTests
{
    [Fact]
    public void Tenant_tier_prefix()
    {
        Assert.Equal("memory-tenant/acme", MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "acme")));
    }

    [Fact]
    public void User_tier_nests_under_tenant()
    {
        Assert.Equal("memory-user/acme/alice", MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(tenantId: "acme", userId: "alice")));
    }

    [Fact]
    public void Session_tier_prefix()
    {
        Assert.Equal("memory-session/s1", MemoryPath.For(MemoryTier.Session, new KnowledgeAccessScope(sessionId: "s1")));
    }

    [Fact]
    public void Null_tenant_renders_the_local_sentinel_and_user_nests_under_it()
    {
        Assert.Equal("memory-user/_local/alice", MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(userId: "alice")));
        Assert.Equal("memory-tenant/_local", MemoryPath.For(MemoryTier.Tenant, KnowledgeAccessScope.Local));
    }

    [Fact]
    public void Fully_local_scope_is_defined_for_every_tier()
    {
        var local = KnowledgeAccessScope.Local;
        Assert.Equal("memory-user/_local/_local", MemoryPath.For(MemoryTier.User, local));
        Assert.Equal("memory-session/_local", MemoryPath.For(MemoryTier.Session, local));
    }
}
