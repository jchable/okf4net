# OKF4net.Catalog.Hosting

`Microsoft.Extensions.DependencyInjection` integration for the
[OKF4net.Catalog](https://www.nuget.org/packages/OKF4net.Catalog) local
knowledge catalog: one extension method, `services.AddKnowledge(...)`, wires
a `catalog.json` manifest into your host's `IServiceCollection`.

This package depends on `Microsoft.Extensions.DependencyInjection.Abstractions`
only — the one explicit exception to OKF4net's zero-dependency policy,
scoped narrowly to DI abstractions (see the [project README](https://github.com/jchable/okf4net)
for the full dependency policy).

## Quick start

```csharp
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

services.AddKnowledge(o => o.AddCatalogFile("./config/catalog.json"));
```

This registers, all as singletons:

- `IKnowledgeCatalog` — a `FileKnowledgeCatalog` over the given manifest.
- `IKnowledgeResolver` — a `DefaultKnowledgeResolver` over that catalog.
- `KnowledgeCatalogOptions` — the resolved catalog file path and catalog root
  (derived from the manifest file's own directory), for callers that want to
  inspect them directly.

Then resolve and use it anywhere in your host:

```csharp
public sealed class SearchEndpoint(IKnowledgeResolver resolver)
{
    public async Task<KnowledgeContext> HandleAsync(string query, CancellationToken ct)
        => await resolver.SearchAsync(new KnowledgeQuery(query), ct);
}
```

Registration is **lazy**: no catalog file is read inside `AddKnowledge`
itself. `configure` runs immediately and its result is validated at
registration time (an `ArgumentException`/`InvalidOperationException` from a
missing or duplicate `AddCatalogFile` call surfaces right away), but the
`catalog.json` file itself is only parsed and path-validated the first time
`IKnowledgeCatalog` (or `IKnowledgeResolver`) is actually resolved from the
container — an invalid manifest surfaces as `CatalogException` from that
first resolve, not from `AddKnowledge`.

## V1 limits

- **Exactly one catalog per `AddKnowledge` call, and exactly one call wins.**
  `AddKnowledge` supports a single `AddCatalogFile` call in its `configure`
  callback in V1 — a second `AddCatalogFile` call inside the *same*
  `configure` callback throws `InvalidOperationException` (`AddBundle`/
  multi-catalog composition is cut as YAGNI; put every source in one
  `catalog.json` instead).
- **A second, separate `AddKnowledge(...)` call on the same
  `IServiceCollection` is silently ignored — the first one wins.** The
  registrations use `TryAddSingleton`, so calling `AddKnowledge` more than
  once (e.g. once from your app and once from a library extension method)
  does not throw and does not error: whichever call ran first determines the
  catalog that ends up registered, and the second call's `configure` is
  still invoked and validated, but its registrations are dropped as
  no-ops. If you need more than one independently-configured catalog in the
  same process, register `IKnowledgeCatalog`/`IKnowledgeResolver` manually
  under distinct keys instead of relying on `AddKnowledge` twice.

See [the OKF4net.Catalog README](https://www.nuget.org/packages/OKF4net.Catalog)
for the catalog model itself (manifest shape, hot-reload behavior, search
semantics), and [the project README](https://github.com/jchable/okf4net) for
full documentation.

Licensed LGPL-3.0-or-later.
