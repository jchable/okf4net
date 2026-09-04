# Security Policy

## Supported versions

Only the latest release of OKF4net receives security fixes.

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report vulnerabilities privately via
[GitHub private vulnerability reporting](https://github.com/jchable/okf4net/security/advisories/new)
(Security tab → "Report a vulnerability"). If that is not an option, email
<julien.chable@gmail.com> with a description, reproduction steps, and impact
assessment.

You can expect an acknowledgement within a few days. Please allow a reasonable
window for a fix to be released before any public disclosure.

## Scope notes

OKF4net parses untrusted markdown/YAML input by design (`okf validate`,
`Bundle.Load`). Crashes, unbounded resource consumption, or path-traversal
issues triggered by crafted bundle content are all in scope.

### Known, assessed dependency advisories

The project website (`web/`) knowingly carries two moderate `react-router`
advisories that cannot be fixed on its dependency tree, with no reachable sink
in a fully static site. The analysis, and what would change the conclusion, is
in [`web/SECURITY-NOTES.md`](web/SECURITY-NOTES.md) — please read it before
reporting them.
