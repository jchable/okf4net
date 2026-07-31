# Design — CLI Ergonomics: Richer Diagnostics + `--json` Output

- **Date** : 2026-07-31
- **Statut** : design validé en brainstorming, prêt pour plan d'implémentation
- **Contexte amont** : `ROADMAP.md` "Next" — "CLI ergonomics: richer diagnostics and machine-readable (`--json`) output where it aids tooling."

## 1. Objectif

`okf validate` et `okf info` ne produisent aujourd'hui que du texte destiné à un humain. Un outil externe (CI, linter, autre programme) qui veut consommer ces résultats doit parser ce texte, ce qui est fragile (le format n'est pas contractuel) et perd de l'information déjà calculée en interne mais non exposée (quel champ frontmatter précis est en cause). Ce design ajoute :
1. Une sortie `--json` sur `validate` et `info`, machine-readable et versionnée par contrat plutôt que par accident de formatage texte.
2. Des diagnostics plus riches : un code stable (`DiagnosticCode`) et le champ frontmatter concerné (`Field`), en plus du message texte déjà existant.

**Hors scope (décidé en brainstorming)** :
- `index`/`graph`/`parse`/`fmt` ne gagnent pas de sortie `--json` dans ce lot (périmètre resserré à `validate`/`info`, qui ont déjà des données structurées naturelles).
- Pas de traçage ligne/colonne dans le parseur YAML — trop invasif, chantier séparé si un jour nécessaire. La granularité de position ajoutée ici s'arrête au nom du champ frontmatter (`generated.at`, `sources.last_modified`, ...), qui est déjà calculé aujourd'hui, juste noyé dans le texte du message.
- Les erreurs de chargement de bundle (`CliOperationException`) ne sont pas structurées en JSON même avec `--json` : elles empêchent tout calcul de diagnostics, donc restent le chemin texte existant (`error: {msg}` sur stderr, code 1), inchangé.

## 2. Contrainte technique clé : Native AOT

La CLI est publiée en Native AOT (`PublishAot`, `TreatWarningsAsErrors`, testée par le job CI "Native AOT publish smoke test"). La sérialisation JSON réflexion-par-défaut de `System.Text.Json` (`JsonSerializer.Serialize(obj)` sans contexte) n'est pas trim-safe et certaines de ses API de réflexion ne fonctionnent pas du tout sous Native AOT.

**Décision : utiliser le mode source-generated** (`JsonSerializerContext` + `[JsonSerializable]`, mode "serialization-optimization"/fast-path). Ce n'est pas un compromis performance — vérifié contre la doc officielle Microsoft (`learn.microsoft.com/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation`) : le mode source-generated est strictement supérieur au mode réflexion sur démarrage, mémoire et débit (jusqu'à 40 %+ de gain mesuré), et c'est de toute façon la seule option viable en Native AOT. Le seul point de couverture réduite (pas de désérialisation fast-path) ne s'applique pas ici : on ne fait que sérialiser en sortie, jamais désérialiser.

`System.Text.Json` fait partie du BCL/SDK .NET — aucune dépendance tierce nouvelle, conforme à la règle zéro-dépendance du projet.

## 3. Modèle de diagnostic étendu (additif, pas de rupture)

`src/OKF4net/Validate.cs` — `Diagnostic` gagne deux membres, `ToString()` ne change pas (les golden fixtures `tests/fixtures/golden/validate*.out` restent identiques au byte près) :

```csharp
public sealed record Diagnostic(
    Severity Severity,
    string? Path,
    ConceptId? Concept,
    string Message,
    DiagnosticCode Code,
    string? Field)
{
    // ToString() inchangé : [severity] path|concept: message
}
```

Nouvel enum `DiagnosticCode`, un membre par diagnostic distinct actuellement émis par `BundleValidator.Validate` (dérivé mécaniquement de chaque site d'émission existant — le message texte ne change pas, on ajoute juste le code + le champ en structuré à côté). Table complète (**36 valeurs** — recompté précisément contre les 36 sites `new Diagnostic(...)` réels dans `src/OKF4net/Validate.cs`, pas la première estimation orale du brainstorming), pour référence et pour que le plan n'ait rien à redécouvrir :

| Code | Message actuel (inchangé) | `Field` |
|---|---|---|
| `UnparseableDocument` | `unparseable concept document: {error}` | `null` |
| `MissingType` | `missing required frontmatter field \`type\`` | `"type"` |
| `MissingRecommendedField` | `missing recommended frontmatter field \`{field}\`` | `field` (dynamique : `title`/`description`/`resource`/`tags`) |
| `GeneratedMissingBy` | `generated is missing required \`by\`` | `"generated.by"` |
| `GeneratedInvalidActor` | `generated.by is not a valid §7 actor: ...` | `"generated.by"` |
| `GeneratedInvalidDate` | `generated.at is not ISO-8601: ...` | `"generated.at"` |
| `VerifiedMissingBy` | `verified entry is missing \`by\`` | `"verified.by"` |
| `VerifiedInvalidActor` | `verified.by is not a valid §7 actor: ...` | `"verified.by"` |
| `VerifiedInvalidDate` | `verified.at is not ISO-8601: ...` | `"verified.at"` |
| `VerifiedEntryNotMapping` | `verified entry is not a \`{by, at}\` mapping` | `"verified"` |
| `VerifiedMalformed` | `verified must be a \`{by, at}\` mapping or a list of them` | `"verified"` |
| `SourceEntryNotMapping` | `source entry is not a mapping` | `"sources"` |
| `SourcesMalformed` | `sources must be a list of entries` | `"sources"` |
| `SourceMissingResource` | `source entry is missing required \`resource\`` | `"sources.resource"` |
| `SourceInvalidLastModified` | `source last_modified is not \`YYYY-MM-DD\`: ...` | `"sources.last_modified"` |
| `UsageWindowInvalidFrom` | `usage_window from is not \`YYYY-MM-DD\`: ...` | `"usage_window.from"` |
| `UsageWindowInvalidTo` | `usage_window to is not \`YYYY-MM-DD\`: ...` | `"usage_window.to"` |
| `StatusNotScalar` | `status is not a scalar \`draft\|stable\|deprecated\`` | `"status"` |
| `StatusUnknown` | `unknown status ...; treated as stable` | `"status"` |
| `StaleAfterInvalid` | `stale_after is not \`YYYY-MM-DD\`: ...` | `"stale_after"` |
| `ConceptStale` | `concept is stale (stale_after ...)` | `"stale_after"` |
| `LegacyCitations` | `body \`# Citations\` is legacy; ...` | `null` (niveau corps, pas frontmatter) |
| `LegacyTimestamp` | `` `timestamp` is a legacy field; prefer `generated.at` `` | `"timestamp"` |
| `ComputationMissingRuntime` | `attested computation missing required 'runtime'` | `"runtime"` |
| `ComputationParameterMissingName` | `parameter entry missing 'name'` | `"parameters"` |
| `ComputationMissingBody` | `attested computation has no computation ...` | `null` |
| `ComputationAmbiguous` | `computation specified both inline and via 'computation:'` | `"computation"` |
| `ExecutorReceiptInvalid` | `executor.receipt is not a list of receipt field names` | `"executor.receipt"` |
| `AttesterResourceEmpty` | `attester.resource is empty` | `"attester.resource"` |
| `FrontmatterPathMissing` | `frontmatter path '{field}' → '...' not found` | `resource.Field` (dynamique) |
| `FrontmatterPathUnsafe` | `frontmatter path '{field}' → '...' escapes the bundle` | `resource.Field` (dynamique) |
| `IndexHasFrontmatter` | `index.md should not contain frontmatter (§8)` | `null` |
| `RootIndexExtraFrontmatter` | `root index.md frontmatter should declare only \`okf_version\` (§12)` | `"okf_version"` |
| `UnsupportedOkfVersion` | `declared okf_version ... is not supported; ...` | `"okf_version"` |
| `LogDateInvalid` | `log date heading is not ISO-8601 \`YYYY-MM-DD\`: ...` | `null` |
| `BrokenLink` | `link target does not resolve to a concept in the bundle: ...` | `null` |

## 4. Sortie `--json`

Nouveau flag `--json` sur `validate`/`info` (même mécanisme que le `HasFlag` existant). Remplace la sortie texte humaine sur stdout ; le code de sortie (0/1) est inchangé. Casse **camelCase** (choix utilisateur, tranche avec le snake_case du vocabulaire OKF lui-même mais plus idiomatique pour un consommateur JSON générique).

`okf validate --json <bundle>` :
```json
{
  "bundle": "<path>",
  "conformant": true,
  "conceptCount": 4,
  "errorCount": 0,
  "warningCount": 5,
  "infoCount": 0,
  "diagnostics": [
    {
      "severity": "warning",
      "code": "LegacyTimestamp",
      "path": "tables/users.md",
      "conceptId": "tables/users",
      "field": "timestamp",
      "message": "`timestamp` is a legacy field; prefer `generated.at`"
    }
  ]
}
```
`path` et `conceptId` ne sont pas mutuellement exclusifs : pour un diagnostic de niveau concept, les deux sont généralement renseignés (le chemin propre du concept, en plus de son id) ; seul un diagnostic de niveau fichier/corps n'a que `path`. Le tableau `diagnostics` conserve l'ordre de `ValidationReport.Diagnostics` (identique à l'ordre d'émission texte actuel) — déterministe, pas retrié par sévérité ou code.

`okf info --json <bundle>` :
```json
{
  "bundle": "<path>",
  "okfVersion": "0.2",
  "conceptCount": 4,
  "indexFileCount": 1,
  "logFileCount": 1,
  "types": { "Table": 2, "Dataset": 1 },
  "linkCount": 8,
  "brokenLinkCount": 1,
  "parseErrors": [{ "path": "...", "message": "..." }]
}
```

`types` est toujours présent, même vide (`{}`) si le bundle n'a aucun concept — pas de logique conditionnelle comme la sortie texte actuelle (`if (byType.Count > 0)`), pour que la forme JSON reste stable quel que soit le contenu du bundle.

DTOs de sortie dédiés (pas les types domaine directement) dans `src/OKF4net.Cli/`, avec un `JsonSerializerContext` source-generated (mode serialization-optimization) les couvrant.

## 5. Plan de test

- Golden fixtures (`tests/fixtures/golden/`) : **intouchées**. Nouveau test explicite pinnant que `validate`/`info` sans `--json` produisent un texte strictement identique à avant (non-régression sur le format actuel).
- `DiagnosticCode`/`Field` : un test par famille de diagnostic (pas les 36 un par un si un échantillon couvre chaque catégorie de construction — dynamique vs statique, frontmatter vs corps vs fichier réservé) vérifiant le bon couple code/champ.
- Sortie JSON : désérialiser (`System.Text.Json`, pas le contexte source-gen — la désérialisation de test peut rester en mode réflexion, seule la CLI elle-même doit être AOT-safe) la sortie de `validate --json`/`info --json` sur `tests/fixtures/appendix_a` et vérifier les clés/valeurs attendues.
- Build : `dotnet publish src/OKF4net.Cli -c Release` (AOT) doit rester propre, zéro warning de trimming/réflexion lié à la sérialisation — vérification manuelle en plus du job CI existant.

## 6. Contraintes respectées

- Zéro dépendance tierce nouvelle (`System.Text.Json` = BCL).
- Native AOT préservé (source-generated, pas réflexion).
- `tests/fixtures/` jamais touché.
- `dotnet format` clean, XML doc sur l'API publique/interne modifiée, SPDX headers sur les nouveaux fichiers.
