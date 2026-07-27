# OKF4net — Plan de communication & contribution

**Date :** 2026-07-27
**Auteur :** Julien Chable
**Statut :** Validé (brainstorming)

## 1. Contexte & objectif

OKF4net est une implémentation .NET (C#, net10.0) zéro-dépendance du format Open
Knowledge Format (OKF v0.1) de Google, publiée sur NuGet, licence LGPL-3.0-or-later,
avec un site sur GitHub Pages. Le projet est techniquement mature (CI multi-OS,
templates d'issues, CONTRIBUTING, CODE_OF_CONDUCT, publication NuGet automatisée).

**Objectif prioritaire de la communication : attirer des contributeurs.**
- Cible primaire à 6 mois : **3–5 contributeurs externes** avec ≥ 1 PR mergée ;
  **1 contributeur récurrent**.
- Indicateurs avancés suivis : GitHub stars, issues ouvertes par des tiers,
  trafic repo (vues/clones), discussions.
- L'adoption (downloads NuGet) est suivie comme **signal**, pas comme métrique primaire.

## 2. Paramètres décidés

| Paramètre | Décision |
|---|---|
| Objectif | Contributeurs (pas adoption/visibilité pure) |
| Public prioritaire | Développeurs .NET/C# **et** builders IA / agents |
| Langue | Anglais d'abord (FR en secondaire) |
| Canaux | LinkedIn, micro-blog (X/Bluesky/Mastodon), blog/dev.to, Reddit/HN |
| Budget temps | 1–2 h / semaine, soutenable |
| Audience de départ | Quasi nulle → stratégie « emprunter l'audience des autres » |
| Onramp contributeur | Prioritaire — construit **avant** toute poussée de com |

## 3. Contraintes structurantes

1. **Audience quasi nulle → emprunter l'audience des autres.** À 1–2 h/semaine, on
   ne peut pas construire une audience propre assez vite. On se branche là où les
   publics .NET et IA-agents sont déjà rassemblés (Show HN, r/dotnet, dev.to,
   newsletters .NET, listes awesome-*, repo OKF amont).
2. **Onramp d'abord → le trafic doit tomber sur quelque chose à faire.** Constat au
   départ : 0 issue ouverte, labels `good first issue` / `help wanted` présents mais
   inutilisés. Sans onramp, chaque pic de visibilité fuit.

## 4. Stratégie retenue

Ossature **« Lancement + audience empruntée » (approche A)**, avec un élément
d'**« embarquement écosystème » (C)** dès le départ (visibilité amont OKF +
awesome-*, gratuit et à fort levier) et le **« build in public » (B)** en régime de
croisière léger (le contenu hebdo construit la marque perso sans surcoût).

Trois phases : **(0) Onramp → (1) Lancement → (2) Entretien & croissance lente.**

## 5. Positionnement & message

Mener par le **bénéfice concret**, jamais par le format (OKF est peu connu). Deux
angles, un par public, même projet :

- **Angle dev .NET :** « Zero-dependency, Native-AOT knowledge bundles for .NET.
  If you can `cat` a file, you can read it; if you can `git clone`, you can ship it. »
  Crochets : zéro dépendance, AOT natif, BCL-only, histoire du port Rust→C# prouvé
  byte-exact.
- **Angle IA/agents :** « Give your AI agents a git-native, human-readable memory. »
  Crochets : `OKF4net.Agents` + Microsoft Agent Framework, mémoire d'agent en
  markdown versionnable vs base vectorielle opaque. Différenciant, surfe sur la
  vague agents.

**Règle éditoriale :** chaque contenu (1) mène avec un bénéfice, (2) montre 5–10
lignes de code ou `okf` en action, (3) finit par « standard ouvert de Google + voici
comment contribuer ». Le format est le contexte, jamais l'accroche.

## 6. Phase 0 — Onramp (semaines 1–3, avant tout post)

Objectif : qu'un dev qui arrive comprenne le projet en 60 s et trouve quoi faire en 5 min.

1. **README « contributeur-first »** — bloc « Why contribute / How to start » en haut,
   pointant vers les good-first-issues.
2. **Roadmap publique** — `ROADMAP.md` + GitHub Projects board (Now / Next / Later).
3. **8–12 issues étiquetées** — mix `good first issue` (petites, cadrées, fichiers
   pointés + critères d'acceptation + test à faire passer) et `help wanted` (plus
   ambitieuses : nouveaux verbes CLI, perfs, exemples d'agents).
4. **`CONTRIBUTING.md`** — vérifier chemin en 3 commandes (build/test/format) + workflow PR.
5. **Activer GitHub Discussions** — lieu bas-friction pour les questions.

**Filet audience-zéro (avant J1) :** PR pour être listé dans **awesome-dotnet** ;
repérer et pré-soumettre aux newsletters (*.NET Weekly*, *The week in .NET*).

*Sortie de Phase 0 : repo « launch-ready ». Rien ne se publie avant.*

## 7. Phase 1 — Lancement (semaine 4, fenêtre ~1 semaine)

Pièce maîtresse = **un article de fond dev.to** (asset permanent, indexé). Séquence
sur ~5 jours pour étaler et pouvoir répondre :

| Jour | Canal | Contenu |
|---|---|---|
| J1 | dev.to (pièce maîtresse) | « I ported a Rust knowledge-format library to zero-dependency .NET — here's what I learned » : histoire (port byte-exact, AOT, zéro-dep) + démo + call-to-contribute |
| J1 | Site/blog perso | Republication **canonical** (SEO long terme) |
| J2 | Show HN | « Show HN: OKF4net – zero-dependency .NET impl of Google's Open Knowledge Format », lien repo ; rester dispo 2–3 h pour répondre |
| J3 | r/dotnet + r/csharp | Post orienté technique .NET (zéro-dep/AOT), participation honnête |
| J4 | LinkedIn + micro-blog (Bluesky/Mastodon) | Version perso/narrative « pourquoi j'ai fait ça » |
| J5 | dev.to ou micro-blog | Angle agents : `OKF4net.Agents`, mémoire d'agent git-native |

**Medium :** cross-post **canonical secondaire** uniquement (copier-coller J1+, avec
`rel=canonical` vers le site). Pas un point de la séquence. Préférer **Hashnode** à
Medium si un jour on veut une 2e plateforme dev — ne pas empiler les deux.

## 8. Phase 2 — Entretien & croissance lente (semaine 5+, ~1–2 h/semaine)

Rituel hebdomadaire par ordre de priorité :

1. **Réactivité d'abord (non négociable)** — répondre à toute issue/PR/discussion sous
   24–48 h. Tueur n°1 de contributeurs = PR ignorée. Si une seule chose est faite dans
   la semaine, c'est ça.
2. **Un contenu léger / semaine** — rotation : « build in public » court (issue résolue,
   choix de design, bench AOT), thread micro-blog, partage d'usage. (Approche B, coût ~0.)
3. **Un geste d'écosystème / mois** — contribution amont OKF, réponse dans une discussion
   Agent Framework, soumission newsletter. (Approche C.)

**Mini-lancements :** chaque release (via la skill `release`) = post d'annonce +
soumission newsletter. Prétextes de com gratuits et récurrents.

## 9. Calendrier récapitulatif

| Quand | Phase | Action clé |
|---|---|---|
| Semaines 1–3 | 0 — Onramp | README contrib-first, ROADMAP, 8–12 issues, Discussions, awesome-dotnet PR |
| Semaine 4 | 1 — Lancement | Séquence J1–J5 (dev.to → HN → Reddit → LinkedIn/micro-blog → agents) |
| Semaine 5+ | 2 — Entretien | Rituel hebdo : réactivité > contenu léger > écosystème ; mini-lancement par release |

## 10. Garde-fous

- **Ne pas lancer avant la fin de Phase 0.** Trafic sans onramp = fuite.
- **Pas de spam multi-communautés.** Chaque post adapté à sa communauté ; Reddit/HN
  sanctionnent l'auto-promo brute.
- **Une accroche = un bénéfice.** Jamais « regardez mon implémentation d'un format
  inconnu ».
- **La réactivité prime sur la production.** Mieux vaut 0 post et répondre aux PR que
  l'inverse.
- **Honnêteté sur l'assistance IA.** Si le port a été assisté par IA, l'assumer
  franchement (angle de contenu possible), ne pas le cacher.

## 11. Hors périmètre (YAGNI)

- Pas de vidéo/YouTube au démarrage (coût trop élevé pour 1–2 h/semaine).
- Pas de traduction systématique bilingue (anglais d'abord).
- Pas de campagne publicitaire payante.
- Pas de Discord/serveur communautaire tant que le volume de discussions ne le justifie
  pas (GitHub Discussions suffit au départ).
