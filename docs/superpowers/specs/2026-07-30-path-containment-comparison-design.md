# Design — Harmonisation de la comparaison de containment des chemins (0.3.2)

- **Date** : 2026-07-30
- **Statut** : design validé en brainstorming, prêt pour plan d'implémentation
- **Contexte amont** : follow-up de la branche OKF §10 Attested Computation ([[okf-attested-computation-0.3.2-followups]] en mémoire) — le fix P1 (`Bundle.TryResolveResource` → `StringComparison.Ordinal` inconditionnel, commit `b220ffa`) a laissé plusieurs sites analogues non harmonisés dans le reste du codebase.

## 1. Objectif

Le fix P1 a établi un principe pour toute frontière de containment de chemin sur entrée non fiable : la case-sensitivity est une propriété du **volume au runtime**, pas de l'OS — une heuristique `IsWindows()||IsMacOS() ? OrdinalIgnoreCase : Ordinal` laisse un échappement ouvert sur un volume monté en case-sensitive (APFS/HFS+ configuré ainsi, Windows avec ReFS/WSL/flag par-dossier). Ce design applique ce principe partout où il manque encore, et referme une brèche que le fix P1 lui-même n'avait pas complètement fermée.

## 2. Inventaire (vérifié par lecture directe du code, pas supposé)

| Site | Aujourd'hui | Nature du check | Traitement |
|---|---|---|---|
| `ReparsePoints.IsWithinBundleRoot` (helper partagé) | `OrdinalIgnoreCase` codé en dur | Escape-prevention — alimente `BundleConceptWriter.cs:415` (containment de la cible d'écriture, dérivée d'un concept-id agent) et `OkfBundleTools.cs:305` (containment du chemin de browse, agent-facing) | → `Ordinal` inconditionnel, dans le helper |
| `ReparsePoints.HasReparsePointAncestor(root, path)` [2-arg] (helper partagé) | `OrdinalIgnoreCase` codé en dur | Escape-prevention — alimente **`Bundle.cs:404`** (brèche résiduelle du fix P1 : le `IsWithin` de ce fichier est déjà `Ordinal`, mais cet appel-ci est resté en 2-arg), `BundleConceptWriter.cs:426,550`, `OkfBundleTools.cs:583,638` | → `Ordinal` inconditionnel, dans le helper |
| `FileMemoryStore.PathComparison` | heuristique OS | Escape-prevention — reparse-ancestor du sous-répertoire mémoire scopé (tenant/user/session, dérivé d'un scope potentiellement non fiable) | → `Ordinal` inconditionnel |
| `MemoryServiceCollectionExtensions.PathComparison` (→ `ThrowIfMemoryOverlapsKnowledge`) | heuristique OS | **Détection de mauvaise configuration au démarrage** — deux racines déjà résolues et configurées par l'opérateur, pas de l'entrée par-requête. Sens de sûreté **inversé** : rater un chevauchement (faux-négatif) = fuite silencieuse mémoire→knowledge ; sur-détecter (faux-positif) = juste une exception au démarrage, sans danger | → **teste les deux comparaisons** (`Ordinal` et `OrdinalIgnoreCase`), lève l'exception si l'une des deux détecte un chevauchement |
| `CatalogPathResolver` | déjà scindé : `ContainmentComparison = Ordinal` (sécurité) / `PathComparison` = heuristique OS (dédup `FusedResolverEngine`, explicitement documentée comme non-sécuritaire) | — | **Aucun changement** — pattern déjà correct, probablement convergé indépendamment sur le même raisonnement que le fix P1 |

## 3. Pourquoi `Ordinal` n'a aucun coût réel pour les checks de containment

Un rejet à tort (« faux-négatif », un chemin légitime rejeté) via `Ordinal` exigerait que le **préfixe-racine** d'un candidat construit légitimement diffère en casse de `Root`. Or tous les candidats de ces checks sont construits par `Path.Combine(racine_ou_parent_connu, suffixe_relatif)` — une opération **purement lexicale** : `Path.Combine`/`Path.GetFullPath` ne consultent jamais le disque pour « corriger » la casse. Le préfixe-racine d'un candidat légitime **conserve donc toujours exactement** la casse de `Root`.

La seule façon d'obtenir un préfixe-racine en casse différente est de remonter via `..` et de redescendre dans une variante de casse de la racine elle-même — **exactement le scénario d'échappement à bloquer**, pas un usage légitime. Une variante de casse **sous** la racine (un opérateur tapant un sous-chemin dans une casse différente) ne pose aucun problème : `IsWithin`/`IsWithinBundleRoot` ne contraignent que le préfixe, et la résolution effective (`Directory.Exists`/`File.Exists`, en aval) passe par l'OS réel, qui gère la casse correctement selon ce que le volume permet réellement.

**Conclusion** : `Ordinal` ne rejette que le cas qu'on veut rejeter. Ce n'est pas un compromis sécurité-contre-commodité — c'est gratuit.

## 4. Détection runtime de la case-sensitivity — envisagée puis écartée

Un mécanisme de sonde runtime (tester si une variante de casse d'un chemin connu-existant résout à la même entrée, mis en cache par racine canonicalisée) a été esquissé en brainstorming comme « le seul choix correct des deux côtés ». Analyse plus poussée (§3 ci-dessus) : pour **chacun** des sites inventoriés, la sonde n'apporterait aucun bénéfice net.
- Sites d'escape-prevention : `Ordinal` seul n'a déjà aucun coût réel (§3) — une sonde ne changerait rien à ce qui est accepté/rejeté en usage légitime.
- Site de détection de mauvaise config (`MemoryServiceCollectionExtensions`) : « tester les deux comparaisons » est déjà strictement plus sûr que n'importe quel choix basé sur une sonde (qui ne testerait qu'UNE des deux).

**Décision : ne pas construire cette sonde.** Construire une mécanique (I/O réelle, cache, cas limites — racine sans lettre à inverser, erreurs de permission pendant la sonde, invalidation) pour un problème qui ne se pose sur aucun site actuel serait de la sur-ingénierie. L'heuristique OS existante (`IsWindows()||IsMacOS() ? OrdinalIgnoreCase : Ordinal`) n'est conservée nulle part comme mécanisme actif après ce lot — cette note documente la décision et son raisonnement, pour éviter qu'une future session ne repose la question sans ce contexte. Si un futur site de comparaison de chemins avec un profil différent apparaît (ex. un chemin absolu réellement fourni par un utilisateur, non construit via `Path.Combine` depuis une racine connue), cette analyse devra être refaite pour CE site spécifique — elle ne se généralise pas automatiquement.

## 5. Plan de test

Tests ajoutés **sur les helpers partagés directement** (`ReparsePoints`), pas sur chaque appelant individuellement — plus efficace et cible précisément le changement réel :
- `IsWithinBundleRoot` : test portable de la comparaison (`Ordinal` rejette un frère en casse différente) + test d'intégration Linux-only (dossiers frères `Bundle`/`bundle`, réutilisant `TempDir.TryCreateJunctionToExternalDir` — même pattern que le fix P1).
- `HasReparsePointAncestor(root, path)` [2-arg] : même paire de tests (portable + Linux-only).
- `FileMemoryStore.PathComparison` : même pattern Linux-only sur son propre chemin de sous-répertoire scopé.
- `MemoryServiceCollectionExtensions.ThrowIfMemoryOverlapsKnowledge` : test **portable** (fonctionne sur toute plateforme) — deux racines qui ne diffèrent que par la casse doivent désormais déclencher le throw, indépendamment de l'OS d'exécution du test.

Suite complète (actuellement 834/834 sur `dev`) doit rester verte ; `dotnet format --verify-no-changes` clean ; aucune régression sur les goldens.

## 6. CHANGELOG / docs

Entrée sous `Fixed`/sécurité dans `[Unreleased]`, dans l'esprit des entrées déjà écrites pour le fix P1 et le fix `ComputationExtractor` (fence-awareness) : élargissement du principe « containment = frontière de sécurité, indépendante de l'OS » aux helpers partagés `ReparsePoints` et à `FileMemoryStore`, plus le durcissement du check de mauvaise configuration mémoire/knowledge.

## 7. Contraintes respectées

- Zéro dépendance tierce nouvelle (BCL only, comme l'existant).
- Comportement inchangé pour tout usage légitime (§3) — durcissement pur, pas de nouvelle fonctionnalité.
- `CatalogPathResolver` non touché (déjà correct).
- SPDX headers, nullable, XML doc sur les membres publics/internes modifiés, `dotnet format` clean.
