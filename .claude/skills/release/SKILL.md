---
name: release
description: >
  Ship a new OKF4net version to nuget.org: version bump, CHANGELOG, release
  notes, tag, CI-driven Trusted Publishing, post-publish verification, GitHub
  Release page. Use this whenever the user asks to release, publish, ship,
  tag, or bump a version ("publie une version", "fais la release", "sors la
  0.2"), asks for release notes or a changelog update, wants to check release
  readiness, or needs to diagnose/retry a failed release workflow — even if
  they only mention one step (e.g. just "tag it" or "update the changelog").
---

# Releasing OKF4net

A release is **a tag push**: `.github/workflows/release.yml` does everything
else (test → pack → Trusted Publishing login → push to nuget.org). Your job is
to get `main` into a releasable state, tag it correctly, watch the pipeline,
and verify the result. Publishing to nuget.org is **irreversible** (packages
can be unlisted, never deleted), so the order of operations below matters:
everything is checked *before* the tag exists.

## 1. Preflight (all gates must pass before touching versions)

- **Work on `main`, in sync with origin.** Other Claude sessions are often
  active in this repo on feature branches — never `git checkout` in the main
  working copy if the tree has changes you didn't make. Instead, work in a
  temporary worktree: `git worktree add <scratchpad>/okf-main main` (remove it
  when done). Update local main without checkout via
  `git fetch origin main:main`.
- **CI is green on main**: `gh run list --workflow=ci.yml --branch main --limit 1`.
  If unmerged work should ship (feature branch, Dependabot PRs), merge it
  first and wait for CI again.
- **Local verification** (catches problems before they burn a tag):
  `dotnet test OKF4net.sln -c Release` and
  `dotnet format OKF4net.sln --verify-no-changes`.
- First release only: the nuget.org **Trusted Publishing policy** must exist
  (owner `jchable`, repo `okf4net`, workflow file `release.yml` — file name
  only) and the `NUGET_USER` GitHub secret must contain the nuget.org
  *profile name* (`gh secret list`). A fresh policy is probationary for 7
  days — a successful publish within that window makes it permanent.

## 2. Pick the version (SemVer)

Inspect what shipped since the last release:

```sh
git describe --tags --abbrev=0          # last release tag
git log --oneline <last-tag>..main
```

- Breaking change to the public library API or CLI behaviour → **major**
  (while pre-1.0: bump **minor** instead, per SemVer §4).
- New features, new API surface, new CLI commands → **minor**.
- Bug fixes, docs, CI, dependency bumps only → **patch**.

The commit prefixes (`feat:`, `fix:`, `chore:`…) map directly onto this. If
the log mixes signals or a change is arguably breaking, propose a version and
ask the user rather than guessing — the version number is a public contract.

## 3. Prepare the release commit

On main (worktree), synchronize **every user-visible version with the tag**:

1. `Directory.Build.props` → `<Version>X.Y.Z</Version>`. This is the local
  default package/assembly version for every project. CI overrides it from
  the tag at pack time, but keeping it synchronized makes local builds,
  metadata, and the tag agree.
2. `src/OKF4net.Cli/OkfCli.cs` → `CliVersion = "X.Y.Z"`. The CLI has an
  explicit version string, so changing MSBuild metadata alone does not
  update `okf --version`.
3. Any user-facing version sample, currently
  `web/src/pages/docs/Cli.tsx` (`versionHtml`), must show the same `X.Y.Z`.
  Search the tracked source tree for the previous version before committing
  to catch further copies: `git grep -n "<previous-version>" -- ':!bin' ':!obj'`.
4. `CHANGELOG.md` (Keep a Changelog format):
   - Rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`.
   - Re-create an empty `## [Unreleased]` section above it.
   - Update the link definitions at the bottom (`[Unreleased]` compare link,
     new `[X.Y.Z]` tag link).
   - If the section is thin, backfill it from the commit log — the CHANGELOG
     is the source for release notes, so write it for users, not committers:
     group by Added/Changed/Fixed, describe behaviour not commits.
   - Never add a bullet to an ALREADY-TAGGED version's section, even on
     discovering work that "really" belongs there chronologically — check
     `git show vX.Y.Z:path/to/file` for the feature in question before
     attributing it to a past release. A retroactive edit misattributes a
     feature to a package that was never actually published with it inside
     (caught once on this repo: a bundle-discovery feature got added to an
     already-published preview's section, though that tag's tree never
     contained the file). New work always goes in `[Unreleased]`, to be
     captured whenever this release process next runs.
5. `README.md`'s **"OKF4net version ↔ OKF spec version" table** (near the
   bottom) gets a new row: `| [X.Y.Z](CHANGELOG.md#anchor) | vN.N | <highlights> |`.
   GitHub's heading-anchor slugifier strips periods and brackets, lowercases,
   and turns each space into a hyphen, so `## [X.Y.Z] - YYYY-MM-DD` becomes
   `#xyz---yyyy-mm-dd` (e.g. `[0.4.0] - 2026-07-30` → `#040---2026-07-30`).
   This table is easy to miss because, unlike the version-sample search in
   step 3 above, there is no previous-version STRING to grep for that would
   reveal the gap — it just silently stops growing release after release.
   Check it every time, not only when something reminds you (missed for two
   releases running on this repo before it was caught).
6. **Minor/major releases only** (new features, new CLI verbs, new public
   API — the same test used in step 2 to pick minor over patch): invoke the
   `update-website` skill now, using the CHANGELOG section you just wrote as
   its primary source of ground truth, and fold any resulting `web/` edits
   into this same commit. Bundling matters because it's atomic — the site
   never spends time describing the previous version after the new one is
   already public — not because it saves a CI run: this commit already
   touches code, so `ci.yml`'s full matrix runs regardless of whether `web/`
   also changed. Skip this step for patch releases (fixes/docs/CI only) —
   `update-website`'s full audit is overkill when nothing user-facing shipped
   beyond what step 3.3 already syncs (the version sample).

Before committing, prove the externally-visible CLI version matches the
release version:

```sh
dotnet run --project src/OKF4net.Cli -- --version
# Expected: okf X.Y.Z (OKF spec v0.2)
```

If step 6 touched `web/`, also run its own verification
(`npm run typecheck && npm run test && npm run build` in `web/`, per the
`update-website` skill) before committing — a broken site build shouldn't
ride along with an otherwise-good release.

Commit as `chore(release): prepare vX.Y.Z`, push main, and **wait for CI**:
`gh run watch $(gh run list --workflow=ci.yml --branch main --limit 1 --json databaseId -q '.[0].databaseId') --exit-status`.

## 4. Tag and publish

```sh
git tag -a vX.Y.Z -m "OKF4net X.Y.Z — <one-line summary>" <release-commit>
git push origin vX.Y.Z
```

The tag push triggers `release.yml`, which derives the pack version from the
tag name (`v` stripped), runs the tests again, packs with
`ContinuousIntegrationBuild=true`, exchanges the job's OIDC token for a
1-hour single-use API key (`NuGet/login@v1`), and pushes with
`--skip-duplicate` (**singular** — `--skip-duplicates` is not a flag and
already failed us once).

Watch it: `gh run watch <run-id> --exit-status` on the latest `release.yml`
run.

## 5. Verify the publish

nuget.org validates and indexes for a few minutes after a successful push.
Poll until the version appears:

```sh
curl -s https://api.nuget.org/v3-flatcontainer/okf4net/index.json
```

(Use a background `until … sleep 30` loop rather than blocking.) Then
spot-check https://www.nuget.org/packages/OKF4net — README rendered, license
`LGPL-3.0-or-later`, version listed.

## 6. GitHub Release page

Create the release from the CHANGELOG section (not `--generate-notes` alone —
the changelog is written for users; generated notes are a commit list):

```sh
gh release create vX.Y.Z --verify-tag --title "OKF4net X.Y.Z" --notes "<extracted CHANGELOG section, plus link to full CHANGELOG>"
```

Append a short install line (`dotnet add package OKF4net --version X.Y.Z`).

## 7. Post-release

- Confirm the README badges resolve (CI, NuGet version).
- If other branches are active (e.g. a phase branch), remind the user to
  `git merge main` there so version/changelog changes propagate.
- Remove the temporary worktree: `git worktree remove <scratchpad>/okf-main`.

## Failure playbook

**Workflow failed before "Push to nuget.org"** (tests, pack, OIDC login):
nothing was published, so the tag can be redone safely. Fix on main, then
delete and re-create the tag on the fixed commit. Tag deletion
(`git push origin --delete vX.Y.Z`) is usually permission-blocked for Claude —
ask the user to run it, then re-tag and re-push. Never leave the fix
uncommitted: parallel sessions have reverted uncommitted work in this repo
before.

**Push step failed** (bad flag, network): same as above — nuget.org received
nothing until the push succeeds.

**Publish succeeded but the release is wrong**: the version on nuget.org is
immutable. Do NOT move or delete the tag — it now points at published
history. Ship a corrected **patch** version and unlist the bad one
(nuget.org web UI → package → Listing, or `dotnet nuget delete` which
unlists, not deletes).

**OIDC login fails**: check the policy fields (workflow file must be
`release.yml` without the `.github/workflows/` path), that `NUGET_USER` is
the profile name (not an email), and that a probationary policy hasn't
expired (re-arm the 7-day window on nuget.org and retry).

**Tag exists but points at the wrong commit and nothing was published**:
treat as "workflow failed" above — user deletes the remote tag, you re-tag.
Verify with `git ls-remote origin refs/tags/vX.Y.Z` before re-pushing.
