// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Catalog;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Scoped (V2) <see cref="OkfContextProvider"/>: split-budget READ (knowledge
/// ∪ memory), scoped user-tier capture WRITE, never-throw, and
/// injection-as-message-not-instructions. Builds a resolver over a fixture-copy
/// knowledge source and a user-tier <see cref="FileMemoryStore"/> over a
/// TempDir; never touches tests/fixtures/ directly.
/// </summary>
public class OkfContextProviderScopedTests
{
    // Microsoft.Agents.AI.AgentSession is abstract with only protected ctors, so
    // `new AgentSession()` does not compile. A sealed no-member subclass IS
    // constructible (AgentSession has no abstract members) and provides the
    // reference identity the provider's ConditionalWeakTable keys on.
    private sealed class TestAgentSession : Microsoft.Agents.AI.AgentSession { }

    private const string MemoryFrontmatter =
        "type: AgentMemory\ntitle: Agent memory\ndescription: x\ntimestamp: 2026-07-27T00:00:00Z\n";

    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    private static (IKnowledgeResolver Resolver, FileMemoryStore Store, TempDir Root) SetUp(TempDir root)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "kb"));
        Directory.CreateDirectory(Path.Combine(root.Path, "mem"));
        root.Write("catalog.json", """
            { "version": 1, "sources": [ { "id": "kb", "path": "./kb", "role": "knowledge" } ] }
            """);

        var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
        var resolver = new DefaultKnowledgeResolver(catalog);
        var store = new FileMemoryStore(new Dictionary<MemoryTier, string> { [MemoryTier.User] = Path.Combine(root.Path, "mem") });
        return (resolver, store, root);
    }

    private static AIContextProvider.InvokingContext Invoking(AgentSession? session, string? userText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var ai = new AIContext { Messages = userText is null ? null : [new ChatMessage(ChatRole.User, userText)] };
#pragma warning disable MAAI001
        return new AIContextProvider.InvokingContext(agent, session, ai);
#pragma warning restore MAAI001
    }

    private static AIContextProvider.InvokedContext Invoked(AgentSession? session, string userText, string agentText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
#pragma warning disable MAAI001
        return new AIContextProvider.InvokedContext(agent, session, [new ChatMessage(ChatRole.User, userText)], [new ChatMessage(ChatRole.Assistant, agentText)]);
#pragma warning restore MAAI001
    }

    private static OkfContextProviderOptions ScopedOptions(KnowledgeAccessScope scope) => new()
    {
        MemoryCapture = MemoryCaptureMode.Enabled,
        CaptureTier = MemoryTier.User,
        ScopeAccessor = _ => scope,
    };

    [Fact]
    public async Task Read_injects_knowledge_as_message_data_never_instructions()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var provider = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(userId: "alice")));

        var result = await provider.ProvideForTest(Invoking(session: null, "orders"), CancellationToken.None);

        Assert.DoesNotContain("orders", result.Instructions ?? string.Empty, StringComparison.Ordinal);
        var text = Assert.Single(result.Messages!).Text;
        Assert.Contains("tables/orders", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_then_recall_round_trips_under_the_user_scope()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var session = new TestAgentSession();
        var provider = new OkfContextProvider(resolver, store, ScopedOptions(scope));
        provider.UtcNow = () => new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

        // Provide first so the scope is correlated to this session, then store.
        await provider.ProvideForTest(Invoking(session, "hello"), CancellationToken.None);
        await provider.StoreForTest(Invoked(session, "remember nonce-zx99", "acknowledged nonce-zx99"));

        Assert.Null(provider.LastMemoryError);
        Assert.True(File.Exists(Path.Combine(root.Path, "mem", "memory-user", "acme", "alice", "2026-07-27.md")));

        // A later provide for the same scope recalls the captured memory.
        var recall = await provider.ProvideForTest(Invoking(session, "nonce-zx99"), CancellationToken.None);
        var text = Assert.Single(recall.Messages!).Text;
        Assert.Contains("nonce-zx99", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_is_scoped_a_different_tenant_recalls_nothing()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var sessionA = new TestAgentSession();
        var providerA = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(tenantId: "a", userId: "alice")));
        providerA.UtcNow = () => new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

        await providerA.ProvideForTest(Invoking(sessionA, "hi"), CancellationToken.None);
        await providerA.StoreForTest(Invoked(sessionA, "tenant-a-secret-qq", "noted qq"));
        Assert.Null(providerA.LastMemoryError);

        var sessionB = new TestAgentSession();
        var providerB = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(tenantId: "b", userId: "bob")));
        await providerB.ProvideForTest(Invoking(sessionB, "hi"), CancellationToken.None);
        var recallB = await providerB.ProvideForTest(Invoking(sessionB, "tenant-a-secret-qq"), CancellationToken.None);

        // Tenant B shares the same knowledge base as tenant A (which contains
        // no such term) and has no memory of its own under this scope, so the
        // strongest possible proof of isolation is legitimately "nothing at
        // all is recalled" (Messages is null) rather than a message that
        // merely happens not to mention the secret -- assert whichever of the
        // two the provider returns never contains it.
        var text = recallB.Messages is null ? string.Empty : Assert.Single(recallB.Messages).Text;
        Assert.DoesNotContain("tenant-a-secret-qq", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_never_throws_when_the_memory_write_fails()
    {
        using var root = new TempDir();
        var (resolver, _, _) = SetUp(root);
        // A store with NO configured tiers: every write is reported, never thrown.
        var emptyStore = new FileMemoryStore(new Dictionary<MemoryTier, string>());
        var session = new TestAgentSession();
        var provider = new OkfContextProvider(resolver, emptyStore, ScopedOptions(new KnowledgeAccessScope(userId: "alice")));

        await provider.ProvideForTest(Invoking(session, "hi"), CancellationToken.None);
        var ex = await Record.ExceptionAsync(async () => await provider.StoreForTest(Invoked(session, "q", "a")));

        Assert.Null(ex);
        Assert.NotNull(provider.LastMemoryError);
    }

    [Fact]
    public async Task Scoped_capture_is_skipped_when_the_scope_cannot_be_correlated()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var provider = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(tenantId: "acme", userId: "alice")));

        // A ScopeAccessor IS configured, but StoreAIContextAsync runs with no
        // session and no prior ProvideAIContextAsync => the scope cannot be
        // correlated, so the capture is skipped (never misfiled into _local).
        await provider.StoreForTest(Invoked(session: null, "q", "a"));

        Assert.NotNull(provider.LastMemoryError);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "mem", "memory-user")));
    }

    [Fact]
    public async Task Reused_session_cannot_misfile_a_prior_invocations_capture_under_a_later_scope()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var scopeX = new KnowledgeAccessScope(tenantId: "tenant-x", userId: "alice");
        var scopeY = new KnowledgeAccessScope(tenantId: "tenant-y", userId: "bob");
        var activeScope = scopeX;
        var provider = new OkfContextProvider(resolver, store, new OkfContextProviderOptions
        {
            MemoryCapture = MemoryCaptureMode.Enabled,
            ScopeAccessor = _ => activeScope,
        });
        provider.UtcNow = () => new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);
        var pooledSession = new TestAgentSession();

        await provider.ProvideForTest(Invoking(pooledSession, "request-x"), CancellationToken.None);
        activeScope = scopeY;
        await provider.ProvideForTest(Invoking(pooledSession, "request-y"), CancellationToken.None);

        await provider.StoreForTest(Invoked(pooledSession, "secret-from-x", "answer-for-x"));

        // FAIL-CLOSED: the pooled session was provided under two different
        // scopes (X then Y), so its correlation box is poisoned and the
        // capture is SKIPPED entirely (recorded in LastMemoryError). The core
        // guarantee is that X's exchange must never surface under the later
        // scope Y; with fail-closed it is not written under X either, so it
        // appears NOWHERE — strictly safer than rewriting it under X, which
        // would still be a guess about which scope the capture belonged to.
        Assert.NotNull(provider.LastMemoryError);

        var readX = await store.ReadAsync(scopeX, new KnowledgeQuery("secret-from-x"));
        var readY = await store.ReadAsync(scopeY, new KnowledgeQuery("secret-from-x"));
        Assert.Empty(readX.Passages);
        Assert.Empty(readY.Passages);
    }

    [Fact]
    public async Task Split_budget_reserves_a_memory_floor_so_memory_is_not_starved_by_knowledge()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var scope = new KnowledgeAccessScope(userId: "alice");
        var session = new TestAgentSession();

        // Pre-seed a memory concept mentioning a distinctive term.
        await store.WriteAsync(scope, new MemoryEntry("2026-07-27", MemoryFrontmatter, "## note\n\nremembered orders detail\n"), MemoryTier.User);

        var provider = new OkfContextProvider(resolver, store, new OkfContextProviderOptions
        {
            ScopeAccessor = _ => scope,
            KnowledgeBudgetShare = 0.5,
            MemoryBudgetShare = 0.5,
        });

        var result = await provider.ProvideForTest(Invoking(session, "orders"), CancellationToken.None);
        var text = Assert.Single(result.Messages!).Text;

        // Both surfaces are represented (memory got its floor share).
        Assert.Contains("memory:User", text, StringComparison.Ordinal);
        Assert.Contains("tables/orders", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Split_budget_honors_both_floors_before_knowledge_spillover()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var scope = new KnowledgeAccessScope(userId: "alice");
        var session = new TestAgentSession();

        // Abundant KNOWLEDGE content: far more matching concepts than fit in
        // a 0.2-share floor of a 200-token budget (each renders ~34 tokens;
        // the 40-token floor below fits only one), so knowledge alone would
        // happily consume the whole budget if handed it -- exactly what the
        // pre-fix formula (knowledgeCap = totalBudget - memoryFloor, ignoring
        // KnowledgeBudgetShare entirely) let it do.
        for (var i = 0; i < 40; i++)
        {
            root.Write($"kb/extra/k{i:D2}.md",
                $"---\ntype: Note\ntitle: Zorbknow item {i:D2}\ndescription: filler\ntimestamp: 2026-07-27T00:00:00Z\n---\n\nZorbknow filler detail line {i:D2} with some padding text for length.\n");
        }

        // Abundant MEMORY content: several distinct day concepts, so memory
        // too would use more than its own floor if given the room.
        for (var i = 0; i < 5; i++)
        {
            await store.WriteAsync(
                scope,
                new MemoryEntry(
                    $"2026-06-{i + 1:D2}",
                    $"type: AgentMemory\ntitle: Quixomem entry {i:D2}\ndescription: x\ntimestamp: 2026-06-{i + 1:D2}T00:00:00Z\n",
                    $"## Quixomem note {i:D2}\n\nQuixomem detail line {i:D2}.\n"),
                MemoryTier.User);
        }

        // Shares that do NOT sum to 1 (0.4 total) -- each a small floor of a
        // small budget, so a real spillover pool exists beyond both floors.
        var provider = new OkfContextProvider(resolver, store, new OkfContextProviderOptions
        {
            ScopeAccessor = _ => scope,
            TokenBudget = 200,
            KnowledgeBudgetShare = 0.2,
            MemoryBudgetShare = 0.2,
        });

        var result = await provider.ProvideForTest(Invoking(session, "zorbknow quixomem"), CancellationToken.None);
        var text = Assert.Single(result.Messages!).Text;

        // Both surfaces are represented.
        Assert.Contains("<okf-context id=\"knowledge:", text, StringComparison.Ordinal);
        Assert.Contains("<okf-context id=\"memory:", text, StringComparison.Ordinal);

        // The genuine regression guard: memory's floor block must render
        // BEFORE knowledge's *spillover* portion -- i.e. memory gets its own
        // reserved share up front, not merely "whatever knowledge left
        // over". Against the pre-fix arithmetic, a single generous
        // knowledge-first pass (sized totalBudget-memoryFloor, never
        // reading KnowledgeBudgetShare) fully consumes abundant knowledge
        // content -- several concepts -- before memory ever gets a look in,
        // so the message's SECOND "<okf-context id=\"knowledge:" block
        // (proving multiple concepts rendered) appears well BEFORE the
        // memory block. The fixed arithmetic reserves and renders both
        // floors first -- knowledge capped at its own small share, fitting
        // exactly one concept -- so the memory block appears immediately
        // after that single floor-sized knowledge block, with knowledge's
        // spillover portion (its second+ concept) rendering only
        // afterward. This was verified empirically against the pre-fix
        // arithmetic: it renders knowledge's first five concepts, then
        // memory, last -- failing this exact assertion.
        const string KnowledgeMarker = "<okf-context id=\"knowledge:";
        var firstMemoryIndex = text.IndexOf("<okf-context id=\"memory:", StringComparison.Ordinal);
        var firstKnowledgeIndex = text.IndexOf(KnowledgeMarker, StringComparison.Ordinal);
        var secondKnowledgeIndex = text.IndexOf(KnowledgeMarker, firstKnowledgeIndex + 1, StringComparison.Ordinal);

        Assert.True(secondKnowledgeIndex >= 0, "need at least two rendered knowledge blocks to distinguish the floor pass from spillover");
        Assert.True(
            firstMemoryIndex < secondKnowledgeIndex,
            "memory's floor block must render before knowledge's spillover portion -- both floors are meant to be honored independently, not just whatever is left after knowledge's own content is exhausted");
    }
}
