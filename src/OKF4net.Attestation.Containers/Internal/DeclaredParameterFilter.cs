// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Filters a run's supplied parameter values down to the ones a concept's
/// contract actually declares, so that a value the caller supplied but the
/// concept never asked for can never reach an executed script or query.
/// <see cref="AttestationOrchestrator.RunAsync"/> only checks that *required*
/// parameters are present — it hands the binder the caller's full,
/// unfiltered dictionary (§10.3 says extras are ignored; nothing in the
/// orchestrator enforces that today), so this allowlist is this host's own
/// responsibility.
/// </summary>
internal static class DeclaredParameterFilter
{
    /// <summary>
    /// Returns a new dictionary containing only the entries of
    /// <paramref name="values"/> whose key names a parameter in
    /// <paramref name="declared"/>, each checked against that parameter's
    /// declared <c>type</c> via <see cref="CheckType"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> FilterAndTypeCheck(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ComputationParameter> declared)
    {
        var result = new Dictionary<string, object?>();
        foreach (var parameter in declared)
        {
            if (values.TryGetValue(parameter.Name, out var value))
            {
                result[parameter.Name] = CheckType(value, parameter.Type);
            }
        }

        return result;
    }

    /// <summary>
    /// Rejects a value whose CLR type is incompatible with a known declared
    /// <paramref name="declaredType"/> string. An unrecognized type name is
    /// accepted as-is (§3's permissive-loading philosophy: judgment on
    /// unknown shapes is deferred, not a hard failure here).
    /// </summary>
    private static object? CheckType(object? value, string? declaredType)
    {
        if (value is null || declaredType is null)
        {
            return value;
        }

        var ok = declaredType switch
        {
            "integer" => value is int or long,
            "number" => value is int or long or float or double or decimal,
            "boolean" => value is bool,
            "string" => value is string,
            "date" => value is DateOnly or DateTime or DateTimeOffset or string,
            _ => true,
        };

        if (!ok)
        {
            throw new ArgumentException($"parameter value for declared type '{declaredType}' has an incompatible CLR type: {value.GetType().Name}");
        }

        return value;
    }
}
