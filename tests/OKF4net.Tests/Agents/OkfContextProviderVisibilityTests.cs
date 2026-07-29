// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Catalog;

namespace OKF4net.Tests.Agents;

/// <summary>
/// <see cref="OkfContextProvider"/>'s scoped (V2) knowledge read: the same
/// <see cref="KnowledgeAccessScope"/> already resolved for the memory read
/// (via <c>ScopeAccessor</c>) now also reaches the knowledge query, so a
/// host-configured <see cref="KnowledgeResolverRouter"/> default visibility
/// policy can restrict what a given caller's invocation ever sees.
/// </summary>
public class OkfContextProviderVisibilityTests
{
    private sealed class TestAgentSession : AgentSession { }

    private static FileKnowledgeCatalog SetUpTenantScopedCatalog(TempDir root)
    {
        root.Write(Path.Combine("acme-kb", "note.md"),
            "---\ntype: Note\ntitle: Orders acme\ndescription: orders\n---\nAcme orders detail.\n");
        root.Write(Path.Combine("beta-kb", "note.md"),
            "---\ntype: Note\ntitle: Orders beta\ndescription: orders\n---\nBeta orders detail.\n");

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "acme-kb", "path": "./acme-kb", "role": "knowledge" },
                { "id": "beta-kb", "path": "./beta-kb", "role": "knowledge" }
              ]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    private static FileMemoryStore EmptyMemoryStore(TempDir root)
    {
        Directory.CreateDirectory(Path.Combine(root.Path, "mem"));
        return new FileMemoryStore(new Dictionary<MemoryTier, string>
        {
            [MemoryTier.User] = Path.Combine(root.Path, "mem"),
        });
    }

    private static AIContextProvider.InvokingContext Invoking(AgentSession? session, string userText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var ai = new AIContext { Messages = [new ChatMessage(ChatRole.User, userText)] };
#pragma warning disable MAAI001
        return new AIContextProvider.InvokingContext(agent, session, ai);
#pragma warning restore MAAI001
    }

    // A tenant may only see the knowledge source whose id starts with its own
    // tenant id, followed by "-" -- a simple, realistic per-tenant rule. Fails
    // CLOSED for a caller with no TenantId: unlike `scope.TenantId ?? ""`
    // (which would make StartsWith("") true for every source, exposing every
    // tenant's catalog to an unscoped caller), a missing tenant id here
    // matches nothing at all. See the mirrored, equally-fixed example in
    // src/OKF4net.Catalog/README.md's "Choosing source visibility" section.
    private static bool TenantPrefixPolicy(KnowledgeAccessScope scope, KnowledgeCatalogSource source) =>
        scope.TenantId is { Length: > 0 } tenantId
        && source.Id.StartsWith(tenantId + "-", StringComparison.Ordinal);

    [Fact]
    public async Task A_router_default_visibility_policy_restricts_what_a_scoped_caller_sees()
    {
        using var root = new TempDir();
        using var catalog = SetUpTenantScopedCatalog(root);

        var resolver = new KnowledgeResolverRouter(catalog, defaultSourceVisibilityPolicy: TenantPrefixPolicy);

        var options = new OkfContextProviderOptions
        {
            TokenBudget = 2000,
            KnowledgeBudgetShare = 1.0,
            MemoryBudgetShare = 0.0,
            MemoryCapture = MemoryCaptureMode.Disabled,
            ScopeAccessor = _ => new KnowledgeAccessScope(tenantId: "acme"),
        };
        var provider = new OkfContextProvider(resolver, EmptyMemoryStore(root), options);

        var result = await provider.ProvideForTest(Invoking(new TestAgentSession(), "orders"), CancellationToken.None);
        var text = Assert.Single(result.Messages!).Text;

        Assert.Contains("knowledge:acme-kb:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("knowledge:beta-kb:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unscoped_caller_sees_no_sources_under_the_tenant_prefix_policy()
    {
        using var root = new TempDir();
        using var catalog = SetUpTenantScopedCatalog(root);

        var resolver = new KnowledgeResolverRouter(catalog, defaultSourceVisibilityPolicy: TenantPrefixPolicy);

        // No ScopeAccessor configured at all -- OkfContextProvider resolves
        // KnowledgeAccessScope.Local (TenantId null), the exact scenario an
        // unauthenticated or not-yet-scoped caller produces. This is the
        // regression guard for the fail-open bug the tenant-prefix policy
        // used to have: an empty "" fallback made StartsWith("") match every
        // source, silently exposing every tenant's catalog to such a caller.
        var options = new OkfContextProviderOptions
        {
            TokenBudget = 2000,
            KnowledgeBudgetShare = 1.0,
            MemoryBudgetShare = 0.0,
            MemoryCapture = MemoryCaptureMode.Disabled,
        };
        var provider = new OkfContextProvider(resolver, EmptyMemoryStore(root), options);

        var result = await provider.ProvideForTest(Invoking(new TestAgentSession(), "orders"), CancellationToken.None);

        Assert.Null(result.Messages);
    }
}
