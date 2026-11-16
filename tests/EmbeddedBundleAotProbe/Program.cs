// SPDX-License-Identifier: LGPL-3.0-or-later
// Prints a deterministic summary of acme_retail, read from this binary's own resources, or with
// "--filesystem <dir>" from disk; ci.yml compares the two (spec §6.4). Fixed clock: no calendar drift.
using OKF4net;
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
IBundleProvider provider = args is ["--filesystem", var dir]
    ? new FileSystemBundleProvider(dir)
    : new EmbeddedResourceBundleProvider(typeof(Marker).Assembly, "acme_retail");

var bundle = await Bundle.LoadAsync(provider); // Bundle.Load(IBundleProvider, …) is internal
var report = BundleValidator.Validate(bundle, clock);
var audit = ConceptAudit.Run(bundle, default, clock);

Console.WriteLine($"concepts={bundle.Concepts.Count}");
foreach (var key in bundle.Concepts.Select(c => c.Key!.ToString()).Order(StringComparer.Ordinal))
{
    Console.WriteLine($"concept {key}");
}

foreach (var line in report.Diagnostics
    .Select(d => $"diagnostic {d.Severity} {d.Code} {d.Key} {d.Field}")
    .Order(StringComparer.Ordinal))
{
    Console.WriteLine(line);
}

Console.WriteLine($"audit concepts={audit.ConceptCount} stale={audit.StaleCount}");
foreach (var (tier, count) in audit.TrustCounts.OrderBy(k => k.Key))
{
    Console.WriteLine($"audit trust {tier}={count}");
}

return 0;

/// <summary>Names this assembly without relying on the generated Program type.</summary>
internal sealed class Marker;
