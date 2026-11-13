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

The project that builds the assembly must hold a **direct** `PackageReference` to this package: a package's `build/` assets do not flow through a `ProjectReference`, so an `OkfBundle` declared in a project that only reaches the package transitively embeds nothing. Not supported: special files (FIFOs, devices) under the bundle directory, which MSBuild cannot tell from regular files and which can hang the build.

## Read

```csharp
using OKF4net;
using OKF4net.Providers.EmbeddedResources;

var provider = new EmbeddedResourceBundleProvider(typeof(App).Assembly, "kb");
var bundle = await Bundle.LoadAsync(provider);
```

The provider is read-only (every write throws `BundleReadOnlyException`), completes every operation synchronously (the library's synchronous entry points work), and versions everything by the assembly's MVID. `EmbeddedResourceBundleProvider.GetBundlePrefixes(assembly)` lists the bundles an assembly carries.

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

## License

LGPL-3.0-or-later. No third-party code.
