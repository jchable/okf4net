// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D1, D10; #141 rule 1.</summary>
public class DependencyGuardTests
{
    [Fact]
    public void The_core_grants_this_package_no_internals()
    {
        // A package that calls an internal of the core breaks at run time (MissingMethodException) when a host
        // pairs it with another version of the core, which NuGet's minimum-version dependency allows.
        var granted = typeof(Bundle).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName);
        Assert.DoesNotContain(granted, name => name.StartsWith("OKF4net.Providers.S3", StringComparison.Ordinal));
    }

    [Fact]
    public void The_assembly_references_only_the_core_the_aws_sdk_and_the_bcl()
    {
        var names = typeof(S3BundleSource).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        var allowed = names.Where(n => n == "OKF4net" || n.StartsWith("AWSSDK.", StringComparison.Ordinal)
            || n.StartsWith("System", StringComparison.Ordinal) || n == "netstandard" || n == "Microsoft.Win32.Primitives");
        Assert.Equal(names.Order(), allowed.Order());
    }
}
