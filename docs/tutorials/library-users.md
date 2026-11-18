# Tutorial: write, index, load and validate a bundle with OKF4net

This tutorial builds a small OKF bundle from C# with the `OKF4net` library,
then checks it with the `okf` command-line tool. By the end you will have:

- written concepts through a provider with `BundleConceptWriter`,
- appended to the bundle's `log.md` (§9),
- regenerated its `index.md` files with `IndexGenerator` (§8),
- loaded it with `Bundle.LoadAsync` and walked its link graph (§6.1),
- validated it with `BundleValidator` (§11) and read the log back with `ChangeLog`,
- run `okf validate`, `okf info`, `okf graph`, `okf verify` and `okf audit` on it.

Section numbers (§n) refer to the OKF v0.2 specification, vendored at
[`docs/spec/SPEC.md`](../spec/SPEC.md).

If you build agents rather than call the library yourself, read
[the agent builders' tutorial](agent-builders.md) instead.

## Before you start

You need the .NET 10 SDK.

**API status.** This tutorial uses the asynchronous provider API
(`IBundleProvider`, `FileSystemBundleProvider`, `Bundle.LoadAsync`, the
`…Async` methods of `BundleConceptWriter`, `IndexGenerator` and
`BundleValidator`). It is on the `dev` branch and is not in OKF4net 0.7.0, the
latest release at the time of writing. Until the next release, reference the
library from a clone of the repository instead of the NuGet package.

Create a console project and reference the library:

```sh
dotnet new console -n BundleTutorial
cd BundleTutorial
dotnet add reference path/to/okf4net/src/OKF4net/OKF4net.csproj
```

Once a release that contains this API is published, `dotnet add package
OKF4net` replaces the last line.

Replace the content of `Program.cs` with the code blocks of steps 1 to 6
below, in order. The file is one program: each step uses variables from the
steps before it. Run it from the project directory with `dotnet run`; it
creates the bundle in `my_bundle/` under the current directory.

## Step 1: a provider and a writer

A bundle is reached through an `IBundleProvider`: a key space of objects that
knows nothing about concepts. `FileSystemBundleProvider` is the one for a
directory, and it can write. `BundleConceptWriter` is the governed write path
for concepts and `log.md`: it validates before writing and writes through the
provider, never around it.

```csharp
using System.Text;
using OKF4net;
using OKF4net.Providers;

const string root = "my_bundle";
Directory.CreateDirectory(root);

var provider = new FileSystemBundleProvider(root);
var options = BundleLoadOptions.Default;
var writer = new BundleConceptWriter(provider, options: options);
```

Pass the writer the same `BundleLoadOptions` you load the bundle with. The
writer checks every key under that policy, so it never creates a file that the
next load would discard.

## Step 2: write concepts

A concept is a markdown file with YAML frontmatter (§4). `WriteConceptAsync`
takes the concept id (the path without `.md`, §2), the frontmatter lines
without the `---` fences, and the body. It runs the producer-grade check first
(non-empty `type`, `title` and `description`), stricter than §11, which only
requires `type`. A document that fails it is not written, and the method
returns the reason as text instead of throwing.

```csharp
Console.WriteLine(await writer.WriteConceptAsync(
    "tables/orders",
    """
    type: Table
    title: Orders
    description: One row per completed customer order.
    tags: [sales, orders]
    generated: { by: human:ada, at: 2026-11-18T09:00:00Z }
    """,
    """
    # Schema

    | Column        | Type   | Description                                 |
    |---------------|--------|---------------------------------------------|
    | `order_id`    | STRING | Unique order identifier.                    |
    | `customer_id` | STRING | Key into [customers](/tables/customers.md). |

    Revenue is defined in [revenue](/metrics/revenue.md).
    """));

Console.WriteLine(await writer.WriteConceptAsync(
    "tables/customers",
    """
    type: Table
    title: Customers
    description: One row per registered customer.
    tags: [sales]
    """,
    "Referenced by [orders](/tables/orders.md).\n"));

Console.WriteLine(await writer.WriteConceptAsync(
    "tables/refunds",
    "title: Refunds\n",
    "This one has no type.\n"));
```

Output:

```text
Written tables/orders (new, 524 bytes). Remember to run okf_regenerate_indexes.
Written tables/customers (new, 143 bytes). Remember to run okf_regenerate_indexes.
Error: Missing required frontmatter keys: type, description
```

The links use the bundle-relative form `/tables/customers.md`, which §6.1
recommends. `/metrics/revenue.md` does not exist: §6.1 makes a broken link
legal, so the writer accepts it. The third write was refused and nothing was
written for `tables/refunds`. The byte counts depend on your source file's
line endings: a raw string literal keeps them, so a file saved with CRLF
writes a slightly longer concept.

The "Remember to run okf_regenerate_indexes" hint is the same text the agent
tool returns; from C#, the equivalent is step 4.

`generated` (§5.2) records who produced the content and when; `human:ada` is a
§7 actor. The writer stores what you give it and does not add a stamp itself.

## Step 3: append to the log

`log.md` is a reserved file (§3.1) holding date-grouped entries, newest first
(§9). `AppendLogAsync` adds one entry under today's UTC date; pass a `DateOnly`
as the third argument to choose another date.

```csharp
Console.WriteLine(await writer.AppendLogAsync("Creation", "Added the orders and customers tables."));
```

Output (your date will differ):

```text
Appended a 'Creation' entry under 2026-11-18 in log.md.
```

## Step 4: regenerate the indexes

An `index.md` lists a directory's contents for progressive disclosure (§8).
`IndexGenerator.RegenerateIndexesAsync` writes one per directory that has
something to list, and returns a `BundleWriteReport`: the keys it wrote and
the ones it could not write, with the reason.

```csharp
BundleWriteReport indexes = await IndexGenerator.RegenerateIndexesAsync(provider, options);
foreach (var key in indexes.Written)
{
    Console.WriteLine($"wrote {key}");
}

Console.WriteLine($"succeeded: {indexes.Succeeded}");
```

Output:

```text
wrote tables/index.md
wrote index.md
succeeded: True
```

The generated `my_bundle/tables/index.md` groups entries by `type` and carries
each concept's `description`, as §8 recommends:

```text
# Table

* [Customers](customers.md) - One row per registered customer.
* [Orders](orders.md) - One row per completed customer order.
```

The root `my_bundle/index.md` links to the subdirectory, and also lists
`log.md` under `# Other`:

```text
# Other

* [log](log.md)

# Subdirectories

* [tables](tables/index.md) - Contains 2: Customers, Orders.
```

## Step 5: load the bundle and walk its links

`Bundle.LoadAsync` lists the bundle through the provider, parses every
non-reserved `.md` file and builds the link graph. Loading is permissive
(§11): a file that cannot be parsed goes to `bundle.ParseProblems` instead of
failing the load, and a broken link stays in the graph with `Exists` false.
Queries on the loaded `Bundle` are synchronous.

```csharp
Bundle bundle = await Bundle.LoadAsync(provider, options);
Console.WriteLine($"{bundle.Count} concepts");
foreach (var concept in bundle.Concepts)
{
    var frontmatter = concept.Document.Frontmatter;
    Console.WriteLine($"  {concept.Id}  [{frontmatter.Type}] {frontmatter.Title}");
}

var orders = ConceptId.Parse("tables/orders");
foreach (var link in bundle.LinksFrom(orders))
{
    Console.WriteLine($"{orders} -> {link.Target} (exists: {link.Exists})");
}

foreach (var source in bundle.Backlinks(orders))
{
    Console.WriteLine($"{orders} <- {source}");
}
```

Output:

```text
2 concepts
  tables/customers  [Table] Customers
  tables/orders  [Table] Orders
tables/orders -> tables/customers (exists: True)
tables/orders -> metrics/revenue (exists: False)
tables/orders <- tables/customers
```

## Step 6: validate, and read the log back

`BundleValidator.ValidateAsync` checks the loaded bundle against §11 and
returns every finding with a severity. Only an `Error` makes a bundle
non-conformant; warnings are soft guidance and `Info` findings are
observations, such as the broken link.

```csharp
ValidationReport report = await BundleValidator.ValidateAsync(bundle);
foreach (var diagnostic in report.Diagnostics)
{
    Console.WriteLine(diagnostic);
}

Console.WriteLine($"conformant: {report.IsConformant} ({report.ErrorCount} errors, {report.WarningCount} warnings)");

await using (BundleObject log = await provider.ReadAsync(BundleKey.Parse("log.md"), expected: null, CancellationToken.None))
using (var reader = new StreamReader(log.Content, Encoding.UTF8))
{
    var changeLog = ChangeLog.Parse(await reader.ReadToEndAsync());
    foreach (var day in changeLog.Days)
    {
        foreach (var entry in day.Entries)
        {
            Console.WriteLine($"{day.Date} {entry.Kind}: {entry.Text}");
        }
    }
}
```

Output (on Linux and macOS the paths use `/`; the log date is the day you ran step 3):

```text
[warning] my_bundle\tables\customers.md: missing recommended frontmatter field `resource`
[warning] my_bundle\tables\orders.md: missing recommended frontmatter field `resource`
[info] tables/orders: link target does not resolve to a concept in the bundle: /metrics/revenue.md
conformant: True (0 errors, 2 warnings)
2026-11-18 Creation: Added the orders and customers tables.
```

`resource` is one of the fields §4.1 recommends, so its absence is a warning,
not an error. `ChangeLog` is the immutable model of a `log.md`: use it to parse
and render a log, and the writer to append to one.

## Step 7: check the bundle with `okf`

`okf` is the command-line tool built on the same library. Install it with
`winget install Coderise.OKF4net`, the install script, or `dotnet tool install
-g OKF4net.Cli` (see the [README](../../README.md#as-a-cli)). The outputs below
come from a build of the `dev` branch, run from the project directory with
`dotnet run --project path/to/okf4net/src/OKF4net.Cli -- <arguments>`.

`okf validate` prints the same findings as step 6 and exits non-zero only when
the bundle is not conformant, so it can gate a CI job:

```sh
okf validate my_bundle
```

```text
[warning] my_bundle\tables\customers.md: missing recommended frontmatter field `resource`
[warning] my_bundle\tables\orders.md: missing recommended frontmatter field `resource`
[info] tables/orders: link target does not resolve to a concept in the bundle: /metrics/revenue.md

2 concept(s); 0 error(s), 2 warning(s), 1 info.
✓ conformant with OKF v0.2
```

`okf info` summarizes the bundle:

```sh
okf info my_bundle
```

```text
bundle:     my_bundle
concepts:   2
index.md:   2
log.md:     1

types:
     2  Table

links:      3 internal (1 broken)
```

`okf graph` prints the link graph; `-x` marks a broken link (add `--dot` for
Graphviz):

```sh
okf graph my_bundle
```

```text
tables/customers
  -> tables/orders
tables/orders
  -> tables/customers
  -x metrics/revenue
```

`okf verify` records a review in a concept's `verified` list (§5.2). A
`human:` actor moves the concept to the `human-reviewed` trust tier (§5.3).
`--at` fixes the timestamp; without it, the stamp is the current time:

```sh
okf verify my_bundle tables/orders --by human:ada --at 2026-11-18T10:00:00Z
```

```text
recorded tables/orders  human:ada  2026-11-18T10:00:00Z
```

`okf audit` reports trust, lifecycle and staleness across the bundle (§5.3 to
§5.5). With a filter flag it prints one line per matching concept:

```sh
okf audit my_bundle --trust unverified
```

```text
tables/customers  no-stale-after  unverified  stable
```

`tables/orders` no longer appears: it is now `human-reviewed`. The columns are
the concept id, its staleness (§5.5; neither concept has `stale_after`), its
trust tier and its status (§5.4; absent means `stable`).

A `verified` stamp is a dated declaration, not a proof: nothing authenticates
`--by`. The README's [`okf verify` section](../../README.md#as-a-cli) explains
what it does and does not guarantee.

## Running the program again

A second `dotnet run` updates the two concepts (`(updated, …)` instead of
`(new, …)`), adds a second `Creation` entry to the log under the same date and
rewrites both indexes; the third write is refused again. `WriteConceptAsync`
replaces the whole document, so the rerun also removes the `verified` stamp
that step 7 added to `tables/orders`. To change a concept without losing
fields you did not write, read it first and write it back conditioned on the
version you read (`BundleWriteCondition.IfMatch`, see
[Writing through a provider](../../README.md#writing-through-a-provider)).

## Where to go next

- **Synchronous entry points.** `Bundle.Load(string)`, `BundleValidator.Validate`,
  and the writer's and the index generator's non-`Async` methods are the same
  implementation behind a bridge that never waits. They serve only a provider
  that declares `Capabilities.CompletesSynchronously`, as
  `FileSystemBundleProvider` does. Prefer the asynchronous methods when the
  provider may be remote.
- **One shared image.** A `BundleHandle` holds the loaded image of one source
  and reloads it according to a refresh policy; a `BundleConceptWriter` built
  over the handle invalidates it after each committed write. The
  [agent builders' tutorial](agent-builders.md) uses one, and the README's
  [`BundleHandle` section](../../README.md#one-image-per-source-bundlehandle)
  describes the policies.
- **Conditional writes, write guarantees and key policies:** the README's
  [Loading through a provider](../../README.md#loading-through-a-provider) and
  [Writing through a provider](../../README.md#writing-through-a-provider).
- **A bundle inside your assembly:**
  [`OKF4net.Providers.EmbeddedResources`](../../src/OKF4net.Providers.EmbeddedResources/README.md).
- **Several bundles behind one resolver:** [`samples/catalog-explorer`](../../samples/catalog-explorer/README.md)
  and [`OKF4net.Catalog`](../../src/OKF4net.Catalog/README.md).
