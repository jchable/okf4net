# OKF4net.Providers.EmbeddedResources

Ship an [OKF](https://github.com/jchable/okf4net) knowledge bundle **inside** your .NET assembly — a single-file or Native AOT agent, a `dotnet tool`, a test assembly — with no directory deployed beside it, and no way for the bundle to drift from the binary you tested.

## Embed

```xml
<ItemGroup>
  <PackageReference Include="OKF4net.Providers.EmbeddedResources" Version="x.y.z" />
  <OkfBundle Include="knowledge/" Prefix="kb" />
</ItemGroup>
```

Every file under `knowledge/` becomes the manifest resource `okf:kb/<relative path>`, byte for byte. Every `Compile`, `EmbeddedResource`, `Content`, `Page`, `ApplicationDefinition` or `Resource` item under the bundle directory (the last three are the WPF SDK's defaults for `*.xaml`), a default one or one you declared, is removed by a target that runs before the build reads those lists (a sanctioned `.cs` script is never compiled into your assembly, a `.resx` never passed to ResGen, whatever the size of your project). The files are then in no build item at all; a `None` item you declare yourself is left alone. `OkfBundle` can be declared anywhere that is evaluated before the build runs, `Directory.Build.targets` included. Several bundles can share an assembly under distinct prefixes. Without an `OkfBundle` item the package's `.targets` does nothing.

A `None` item with `CopyToOutputDirectory`/`CopyToPublishDirectory` on the bundle's files copies them beside the binary too — which works, but defeats the purpose. The same on a `Content` item has no effect: it is detached.

The project that builds the assembly must hold a **direct** `PackageReference` to this package: a package's `build/` assets do not flow through a `ProjectReference`, so an `OkfBundle` declared in a project that only reaches the package transitively embeds nothing. Special files (FIFOs, devices, sockets) under the bundle directory are refused outside Windows (`OKFEMB008`): MSBuild reads them as regular files and the build would block on them. The check runs the platform's `find`, which must be on the `PATH`: a `find` that is missing or fails is refused too (`OKFEMB009`), never taken for "nothing found".

## Read

```csharp
using OKF4net;
using OKF4net.Providers.EmbeddedResources;

var provider = new EmbeddedResourceBundleProvider(typeof(App).Assembly, "kb");
var bundle = await Bundle.LoadAsync(provider);
```

The provider is read-only (`Capabilities.CanWrite` is `false`: every write or delete fails with `BundleReadOnlyException`, so `BundleConceptWriter` throws over it and `OkfBundleTools` in `OKF4net.Agents` offers no write tool), completes every operation synchronously (`Capabilities.CompletesSynchronously`: the library's synchronous members over a provider, `Bundle.ReadResourceText(BundleKey)` or `Bundle.TryResolveResourceKey` say, accept it), and versions the bundle and every object by one token, the manifest module's MVID (the content cannot change while the process runs, so a `BundleHandle` over it never reloads anything new). It declares only `BundleKeyConstraints.WellFormedUnicode` and applies no bound itself: the core applies `BundleLimits` and the key policy to what it lists, as for any provider. The constructor throws `ArgumentException` when the assembly holds no resource under that prefix. `EmbeddedResourceBundleProvider.GetBundlePrefixes(assembly)` lists the bundles an assembly carries.

`provider.Location` (`embedded:<assembly>/<prefix>`) and every display path derived from it (`Concept.Path`, `Diagnostic.Path`) are for display only: read through keys (`Bundle.TryResolveResourceKey`, `Bundle.ReadResourceText(BundleKey)`). The obsolete path-based members throw `NotSupportedException` on such a bundle.

Supported assemblies: those loaded at run time from a file, a single-file bundle, a Native AOT image or bytes (`Assembly.Load(byte[])`), with resources embedded in their manifest module. A dynamic assembly is refused (`ArgumentException`); linked resources and multi-module assemblies are not supported.

## Build errors

| Code | Meaning |
|---|---|
| `OKFEMB001` | `Prefix` missing, empty, or containing `/`. |
| `OKFEMB002` | The directory does not exist, or holds no file. |
| `OKFEMB003` | Two `OkfBundle` items share a prefix, or name the same directory (compared ignoring case). |
| `OKFEMB004` | A file name an assembly cannot hold: `\` (outside Windows), `"`, `|`, a control character, or two files that differ only by case, or that read as the same path once a `\` is a separator. |
| `OKFEMB005` | A symbolic link or junction under the bundle directory (or the directory itself). Links are refused, never followed: a link to `~/.ssh` must not end up in a published binary. |
| `OKFEMB006` | A target of your own put a bundle file back into `Compile`, `Content`, `EmbeddedResource`, `Page`, `ApplicationDefinition` or `Resource` after the package detached it (a `Compile` or `EmbeddedResource` one is also caught just before and just after the compiler runs, whatever the target's hooks). |
| `OKFEMB007` | The bundle directory is, or holds, the project directory: it would embed `obj/`, `bin/` and the project's own files. Point `OkfBundle` at a subdirectory. A directory that directly holds a file named like the project file is refused whatever the spelling of its path (8.3 alias, link, `subst` drive); a bundle directory that holds the project in a subdirectory is recognised by its path text only. |
| `OKFEMB008` | A FIFO, device or socket under the bundle directory (outside Windows). |
| `OKFEMB009` | Outside Windows, `find` could not inspect the bundle directory (missing from the `PATH`, unreadable subdirectory, any failure): the check fails closed instead of reading nothing as "no special file". |

## In a knowledge catalog

`OKF4net.Catalog` reads `catalog.json` sources through `IBundleProviderFactory`. Until a built-in factory exists ([#236](https://github.com/jchable/okf4net/issues/236)), register your own:

```csharp
sealed class EmbeddedFactory(Assembly assembly) : IBundleProviderFactory
{
    public string ProviderType => "embedded";
    public IReadOnlySet<string> AcceptedSettings { get; } = new HashSet<string> { "prefix" };
    public IBundleProvider Create(KnowledgeCatalogSource source) =>
        new EmbeddedResourceBundleProvider(assembly, source.Settings["prefix"]);
}
```

Pass the assembly explicitly: never let a manifest choose what code is loaded.

## Tests

From a clone of [the repository](https://github.com/jchable/okf4net), at its root (.NET SDK 10.0+). The provider's tests include `TargetsBuildTests`, which runs real child `dotnet build`s that import the `.targets` at NuGet's own import point: allow several minutes. Some cases depend on the platform and are skipped elsewhere: FIFOs, `find` and backslash names run outside Windows only, case twins on a case-sensitive volume only, 8.3 aliases and WPF on Windows only, links only where the process may create one. CI runs them on Linux, macOS and Windows:

```sh
dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~OKF4net.Tests.Providers.EmbeddedResources"
dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~TargetsBuildTests"   # the .targets alone
```

The Native AOT check consumes the **packages**, as a user would: it packs the core and this provider at a version no feed holds, publishes `tests/EmbeddedBundleAotProbe` (outside `OKF4net.sln`, it embeds `bundles/acme_retail/`) Native AOT from them, and compares the binary's summary of its embedded bundle with its summary of the same directory on disk. CI runs it on Linux, macOS and Windows (`embedded bundle AOT smoke test` in `.github/workflows/ci.yml`). Locally, in PowerShell 7 from the repository root, replacing `win-x64` with your RID:

```powershell
$v = "0.0.0-local.$(Get-Date -Format yyyyMMddHHmmss)"
$work = Join-Path ([IO.Path]::GetTempPath()) "okf-probe-$v"
$env:NUGET_PACKAGES = "$work/packages"   # an isolated cache: the local version never reaches yours
foreach ($p in 'src/OKF4net/OKF4net.csproj', 'src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj') {
  dotnet pack $p -c Release -o "$work/feed" "-p:Version=$v"
  if ($LASTEXITCODE -ne 0) { throw "pack failed: $p" }
}
dotnet publish tests/EmbeddedBundleAotProbe/EmbeddedBundleAotProbe.csproj -c Release -r win-x64 "-p:OkfProbeVersion=$v" "-p:RestoreAdditionalProjectSources=$work/feed" -o "$work/out"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$embedded = @(& "$work/out/EmbeddedBundleAotProbe")
if ($LASTEXITCODE -ne 0) { throw "embedded run exited $LASTEXITCODE" }
$filesystem = @(& "$work/out/EmbeddedBundleAotProbe" --filesystem bundles/acme_retail)
if ($LASTEXITCODE -ne 0) { throw "filesystem run exited $LASTEXITCODE" }
if (-not ($embedded[0] -match '^concepts=[1-9]')) { throw "no concept loaded: $($embedded[0])" }
if (Compare-Object $embedded $filesystem -SyncWindow 0) { throw "the two summaries differ" }
"OK: $($embedded.Count) identical summary lines"
Remove-Item Env:NUGET_PACKAGES
```

Native AOT links with the platform's native toolchain ([prerequisites](https://aka.ms/nativeaot-prerequisites); on Windows, the Desktop development with C++ workload). Without it the publish fails with `Platform linker not found`; adding `-p:PublishAot=false` to the publish still checks the packages, the `.targets` at NuGet's import point and the embedded-versus-disk comparison, but not the AOT image itself.

## License

LGPL-3.0-or-later. No third-party code.
