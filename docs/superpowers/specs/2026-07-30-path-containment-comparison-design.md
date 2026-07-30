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
| `ReparsePoints.HasReparsePointAncestor(root, path)` [2-arg] (helper partagé) | `OrdinalIgnoreCase` codé en dur | Escape-prevention — alimente **`Bundle.cs:404`** (brèche résiduelle du fix P1 : le `IsWithin` de ce fichier est déjà `Ordinal`, mais cet appel-ci est resté en 2-arg), `BundleConceptWriter.cs:426,550`, `OkfBundleTools.cs:307,583,638` | → `Ordinal` inconditionnel, dans le helper |
| `FileMemoryStore.PathComparison` | heuristique OS | Escape-prevention — reparse-ancestor du sous-répertoire mémoire scopé (tenant/user/session, dérivé d'un scope potentiellement non fiable) | → `Ordinal` inconditionnel |
| `MemoryServiceCollectionExtensions.PathComparison` (→ `ThrowIfMemoryOverlapsKnowledge`) | heuristique OS | **Détection de mauvaise configuration au démarrage** — deux racines déjà résolues et configurées par l'opérateur, pas de l'entrée par-requête. Sens de sûreté **inversé** : rater un chevauchement (faux-négatif) = fuite silencieuse mémoire→knowledge ; sur-détecter (faux-positif) = juste une exception au démarrage, sans danger | → `OrdinalIgnoreCase` **inconditionnel** (voir §3bis : prouvé strictement équivalent à « tester les deux et rejeter si l'une le dit », donc pas besoin de tester les deux — supprimer le champ heuristique OS, pas le remplacer par un double test) |
| `CatalogPathResolver` | déjà scindé : `ContainmentComparison = Ordinal` (sécurité) / `PathComparison` = heuristique OS (dédup `FusedResolverEngine`, explicitement documentée comme non-sécuritaire) | — | **Aucun changement** — pattern déjà correct, convergé indépendamment sur le même raisonnement que le fix P1 |
| `IndexGenerator.cs` (wrapper privé `HasReparsePointAncestor`, lignes 436-441) | déjà `StringComparison.Ordinal` codé en dur (avec un raisonnement dans son propre commentaire de doc quasi identique à celui du fix P1) | Re-check TOCTOU tardif avant chaque écriture d'`index.md` | **Aucun changement** — 3e convergence indépendante sur le même pattern, appuie la confiance dans le §3 |

## 3. Pourquoi `Ordinal` n'a aucun coût réel pour les checks de containment

Un rejet à tort (« faux-négatif », un chemin légitime rejeté) via `Ordinal` exigerait que le **préfixe-racine** d'un candidat construit légitimement diffère en casse de `Root`. Or tous les candidats de ces checks sont construits par `Path.Combine(racine_ou_parent_connu, suffixe_relatif)` — une opération **purement lexicale** : `Path.Combine`/`Path.GetFullPath` ne consultent jamais le disque pour « corriger » la casse. Le préfixe-racine d'un candidat légitime **conserve donc toujours exactement** la casse de `Root`.

La seule façon d'obtenir un préfixe-racine en casse différente est de remonter via `..` et de redescendre dans une variante de casse de la racine elle-même — **exactement le scénario d'échappement à bloquer**, pas un usage légitime. Une variante de casse **sous** la racine (un opérateur tapant un sous-chemin dans une casse différente) ne pose aucun problème : `IsWithin`/`IsWithinBundleRoot` ne contraignent que le préfixe, et la résolution effective (`Directory.Exists`/`File.Exists`, en aval) passe par l'OS réel, qui gère la casse correctement selon ce que le volume permet réellement.

**Conclusion** : `Ordinal` ne rejette que le cas qu'on veut rejeter. Ce n'est pas un compromis sécurité-contre-commodité — c'est gratuit.

## 3bis. Pourquoi `OrdinalIgnoreCase` seul suffit (au lieu de « tester les deux comparaisons »)

Le brainstorming avait retenu, pour `ThrowIfMemoryOverlapsKnowledge` : « vérifier les deux comparaisons, throw si l'une des deux dit chevauchement » — soit, pour deux racines A et B, rejeter si l'une quelconque de `IsWithin(A,B,Ordinal)`, `IsWithin(A,B,OrdinalIgnoreCase)`, `IsWithin(B,A,Ordinal)`, `IsWithin(B,A,OrdinalIgnoreCase)` est vraie.

Pour une paire de chaînes fixée, `Ordinal` est strictement plus strict que `OrdinalIgnoreCase` (une égalité caractère-à-caractère implique trivialement une égalité insensible à la casse) : donc `IsWithin(x,y,Ordinal) == true` implique toujours `IsWithin(x,y,OrdinalIgnoreCase) == true`. Le terme `Ordinal` de chaque paire n'ajoute donc aucun cas que le terme `OrdinalIgnoreCase` correspondant ne couvre déjà (loi d'absorption : `P ∨ Q ≡ Q` quand `P ⟹ Q`). Le check à 4 termes se réduit exactement à 2 termes, sans changer le résultat pour **aucune** paire (A, B) possible :

```
IsWithin(A,B,OrdinalIgnoreCase) || IsWithin(B,A,OrdinalIgnoreCase)
```

C'est cette forme réduite qui remplace le champ `PathComparison` heuristique OS dans le tableau du §2 — pas un abandon du principe « tester les deux sens », juste la suppression d'une redondance prouvée.

## 4. Détection runtime de la case-sensitivity — envisagée puis écartée

Un mécanisme de sonde runtime (tester si une variante de casse d'un chemin connu-existant résout à la même entrée, mis en cache par racine canonicalisée) a été esquissé en brainstorming comme « le seul choix correct des deux côtés ». Analyse plus poussée (§3 ci-dessus) : pour **chacun** des sites inventoriés, la sonde n'apporterait aucun bénéfice net.
- Sites d'escape-prevention : `Ordinal` seul n'a déjà aucun coût réel (§3) — une sonde ne changerait rien à ce qui est accepté/rejeté en usage légitime.
- Site de détection de mauvaise config (`MemoryServiceCollectionExtensions`) : le check retenu (`OrdinalIgnoreCase` inconditionnel, §3bis — mathématiquement équivalent à tester les deux comparaisons) est déjà strictement plus sûr que n'importe quel choix basé sur une sonde, qui ne testerait qu'UNE des deux comparaisons — celle correspondant à la case-sensitivity détectée à l'instant de la sonde.

**Décision : ne pas construire cette sonde.** Construire une mécanique (I/O réelle, cache, cas limites — racine sans lettre à inverser, erreurs de permission pendant la sonde, invalidation) pour un problème qui ne se pose sur aucun site actuel serait de la sur-ingénierie. L'heuristique OS existante (`IsWindows()||IsMacOS() ? OrdinalIgnoreCase : Ordinal`) n'est conservée comme mécanisme actif qu'à un seul endroit après ce lot : `CatalogPathResolver.PathComparison` (voir le tableau du §2), un champ de dédup non-sécuritaire explicitement hors scope de ce lot ; partout ailleurs elle est remplacée par un choix inconditionnel (`Ordinal` ou `OrdinalIgnoreCase` selon le site). Cette note documente la décision et son raisonnement, pour éviter qu'une future session ne repose la question sans ce contexte. Si un futur site de comparaison de chemins avec un profil différent apparaît (ex. un chemin absolu réellement fourni par un utilisateur, non construit via `Path.Combine` depuis une racine connue), cette analyse devra être refaite pour CE site spécifique — elle ne se généralise pas automatiquement.

## 5. Plan de test

Tests ajoutés **sur les helpers partagés directement** (`ReparsePoints`), pas sur chaque appelant individuellement — plus efficace et cible précisément le changement réel :
- `IsWithinBundleRoot` : test portable de la comparaison (`Ordinal` rejette un frère en casse différente) + test d'intégration Linux-only (dossiers frères `Bundle`/`bundle`, réutilisant `TempDir.TryCreateJunctionToExternalDir` — même pattern que le fix P1).
- `HasReparsePointAncestor(root, path)` [2-arg] : même paire de tests (portable + Linux-only).
- `FileMemoryStore.PathComparison` : même pattern Linux-only sur son propre chemin de sous-répertoire scopé.
- `MemoryServiceCollectionExtensions.ThrowIfMemoryOverlapsKnowledge` : test **portable** (fonctionne sur toute plateforme) — deux racines qui ne diffèrent que par la casse doivent désormais déclencher le throw, indépendamment de l'OS d'exécution du test.
- **Note** : `OkfBundleTools.cs:583` appelle `HasReparsePointAncestor(BundleRoot, BundleRoot)` — les deux arguments sont identiques. La boucle interne du helper (`while (!string.Equals(current, root, rootComparison))`) sort dès le premier tour puisque `current == root` immédiatement, donc cet appel retourne `false` quel que soit le `StringComparison` utilisé : un no-op structurel, pas une cible de test pertinente pour ce fix.

Suite complète (actuellement 834/834 sur `dev`) doit rester verte ; `dotnet format --verify-no-changes` clean ; aucune régression sur les goldens.

## 6. CHANGELOG / docs

Entrée sous `Fixed`/sécurité dans `[Unreleased]`, dans l'esprit des entrées déjà écrites pour le fix P1 et le fix `ComputationExtractor` (fence-awareness) : élargissement du principe « containment = frontière de sécurité, indépendante de l'OS » aux helpers partagés `ReparsePoints` et à `FileMemoryStore`, plus le durcissement du check de mauvaise configuration mémoire/knowledge.

## 7. Contraintes respectées

- Zéro dépendance tierce nouvelle (BCL only, comme l'existant).
- Comportement inchangé pour tout usage légitime sur les 3 sites d'escape-prevention du §2 (`IsWithinBundleRoot`, `HasReparsePointAncestor` 2-arg, `FileMemoryStore.PathComparison` — voir §3) — durcissement pur, pas de nouvelle fonctionnalité sur ces trois lignes.
- Changement de comportement **intentionnel** sur le 4e site (`MemoryServiceCollectionExtensions.ThrowIfMemoryOverlapsKnowledge` — voir §3bis) : une configuration mémoire/knowledge qui se chevauche uniquement par variante de casse, et qui pouvait passer silencieusement sous certaines combinaisons de l'ancienne heuristique OS, déclenche désormais toujours l'exception de démarrage — c'est le but recherché, pas un effet de bord à minimiser.
- `CatalogPathResolver` non touché (déjà correct).
- SPDX headers, nullable, XML doc sur les membres publics/internes modifiés, `dotnet format` clean.
