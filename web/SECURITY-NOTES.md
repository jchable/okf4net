# Known dependency advisories

`npm audit` on this project reports **3 moderate vulnerabilities**, which are
**2 distinct advisories** deliberately carried rather than fixed. The counts
differ because npm counts affected *packages*: both advisories are in
`react-router` itself, and `react-router-dom` and `vite-react-ssg` are each
counted again for depending on it. This file records why they are carried, so
the next audit — automated or human — does not have to re-derive it.

Reassess whenever `vite-react-ssg` gains React Router 7 support, or when the
site starts routing on anything a visitor controls.

## The two carried advisories

Both are in `react-router`, reached transitively through `react-router-dom` and
`vite-react-ssg`:

| Advisory | Severity | Summary |
|---|---|---|
| [GHSA-wrjc-x8rr-h8h6](https://github.com/advisories/GHSA-wrjc-x8rr-h8h6) | moderate | Open redirect via backslash in `<Link>` and `useNavigate` |
| [GHSA-337j-9hxr-rhxg](https://github.com/advisories/GHSA-337j-9hxr-rhxg) | moderate | Arbitrary constructor injection via `deserializeErrors()` during SSR hydration |

Everything else `npm audit` used to report **has** been fixed: `nanoid` was
raised to 3.3.18 (clearing a high-severity advisory), and `react-router-dom` to
6.30.6 (clearing [GHSA-jjmj-jmhj-qwj2](https://github.com/advisories/GHSA-jjmj-jmhj-qwj2)).

## Why they are not fixed

Both are fixed only in **React Router 7.18.0** — their affected range is
`>=6.0.0 <7.18.0`, so there is no patched release on the 6.x line and `npm
audit` reports "No fix available".

React Router 7 is not reachable from here. `vite-react-ssg` — this site's
static-site generator, already at its latest release (0.9.2) — declares
`"react-router-dom": "^6.14.1"` as a peer dependency. Fixing these advisories
therefore means replacing the SSG framework, not upgrading a package.

## Why that is an acceptable trade

Neither advisory has a reachable sink in this site. Verified by inspection of
`web/src`:

- **No user-controlled navigation targets.** Every `<Link to>` is built from a
  hardcoded array (`SiteBar`'s nav items) or a compile-time slug
  (`DocsSidebar`). Nothing routes on a query string, a hash, or any other
  visitor input, so the open-redirect path is never entered.
- **No imperative navigation at all.** There is no `useNavigate`, no
  `<Navigate>`, and no `redirect()` anywhere in `src/`.
- **No data router.** The site does not use `createBrowserRouter`, so the
  `deserializeErrors()` SSR-hydration path the second advisory depends on is
  never executed.

The site is also fully static: it is pre-rendered and served from GitHub Pages,
with no server-side request handling.

Replacing the SSG framework to close two advisories with no reachable sink
would be a large, risky change bought for no actual security gain — and the
migration itself would be far more likely to introduce a defect than these
advisories are to be exploited here.

## What guards this

CI (`.github/workflows/web-ci.yml`) runs `npm audit --audit-level=high`, so any
**new** high or critical advisory fails the build while these two assessed
moderates stay quiet. The alternative — a strict gate plus a suppression list —
was rejected because a stale suppression silently hides the next real finding.

Dependabot remains enabled on the repository and will still open PRs for these
packages if a fix appears on the 6.x line.
