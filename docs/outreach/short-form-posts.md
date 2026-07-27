# OKF4net — short-form launch posts

Ready-to-paste drafts for the OKF4net launch window. Each section names its
target destination, suggested timing (J1–J5 relative to launch day), and the
one clear CTA it carries. Replace `https://dev.to/REPLACE-WITH-ARTICLE-URL`
with the real dev.to article URL once it is published — every post that
links the *article* (not the repo) uses this placeholder and must be updated
before posting.

Repo: https://github.com/jchable/okf4net

---

## 1. Show HN — J2

**Destination:** https://news.ycombinator.com/submit
**Link submitted:** the REPO (`https://github.com/jchable/okf4net`), not the article.
**Reminder:** post this in a window where you can stay online and reply for
2–3 hours afterward — HN traffic and questions cluster in the first hour or
two, and an unanswered Show HN loses momentum fast.

### Title

```
Show HN: OKF4net – zero-dependency .NET impl of Google's Open Knowledge Format
```

### First-comment body

```
OKF (Open Knowledge Format) is Google's spec for representing knowledge as a
directory of markdown files with YAML frontmatter — no database, just files
you can `cat`, `grep`, and `git clone`. OKF4net is a from-scratch C# port for
.NET: the core library and CLI have zero third-party runtime dependencies
(BCL only, including a hand-written YAML-subset parser and markdown link
scanner), and the `okf` CLI publishes as a single self-contained Native AOT
binary. I built it because I wanted an OKF implementation that fits .NET's
constraints (no runtime install, no dependency surface) and could plug
straight into agent tooling — there's an optional layer on top
(OKF4net.Agents) that exposes bundle read/write/search as Microsoft Agent
Framework tools, with a context provider that captures agent memory as
plain git-diffable markdown. The trickiest part was proving byte-exact
parity against this repo's former Rust implementation before retiring it
(the full suite passes end-to-end, including golden CLI comparisons) — happy
to go into that or anything else in the code. It's young and I'd genuinely welcome feedback,
especially from anyone who's built similar file-based knowledge or memory
systems.
```

---

## 2. Reddit — r/dotnet and r/csharp — J3

**Link submitted (both):** the ARTICLE —
`https://dev.to/REPLACE-WITH-ARTICLE-URL`
**Reminder:** read each subreddit's self-promotion rules before posting
(both generally want genuine technical content over a bare link-drop, and
some require a certain account history or a "Saturday self-promo" style
thread) — engage honestly in the comments, answer questions plainly, and
don't reflexively defend every design choice.

### r/dotnet

Angle: the .NET-ecosystem practicalities — zero-dep, Native AOT, cross-platform CI, fits into agent stacks.

**Title:**
```
I ported a Google spec (Open Knowledge Format) to a zero-dependency .NET library + Native AOT CLI
```

**Body:**
```
OKF (Open Knowledge Format) is Google's spec for storing knowledge as
directories of markdown files with YAML frontmatter — cross-linked,
git-friendly, no database required. I wrote OKF4net, a from-scratch C#
implementation targeting net10.0.

What might interest this sub specifically:

- **Zero third-party runtime dependencies** in the core library and CLI —
  BCL only. That meant writing an in-repo YAML-subset parser, a markdown
  link scanner, and CLI arg parsing by hand rather than pulling in
  YamlDotNet/System.CommandLine/etc.
- The `okf` CLI (`validate`/`info`/`index`/`graph`/`parse`/`fmt`) publishes
  as a self-contained **Native AOT** single-file binary — no .NET runtime
  needed on the target machine, and invocations are drop-in compatible with
  the Rust binary it replaced.
- It was ported from this repo's former Rust `okf` implementation, verified
  byte-exact against captured Rust CLI output (the full suite passes
  end-to-end, including five golden CLI comparisons) before the Rust source
  was deleted.
- There's an optional `OKF4net.Agents` package (the only one that takes a
  dependency — on Microsoft.Agents.AI) exposing bundle operations as agent
  function tools, if you're wiring LLM agents into a codebase or knowledge
  base.

Full writeup: https://dev.to/REPLACE-WITH-ARTICLE-URL
Repo: https://github.com/jchable/okf4net

It's an early, small project — feedback on the API surface or the AOT setup
particularly welcome.
```

### r/csharp

Angle: the language/design-craft side — hand-rolled parser, order-preserving frontmatter model, nullable/warnings-as-errors, golden-file testing discipline.

**Title:**
```
Writing a YAML-subset parser and a byte-exact ported CLI in C# with zero dependencies — lessons from OKF4net
```

**Body:**
```
Sharing a small-but-opinionated C# project: OKF4net, an implementation of
Google's Open Knowledge Format (knowledge-as-markdown-with-YAML-frontmatter)
for .NET. A few things that were genuinely interesting to design in C#:

- **Frontmatter is an order-preserving `YamlMapping` with typed getters
  layered on top**, not a fixed DTO — deserializing to a fixed type would
  silently drop producer-defined keys, which the spec requires you to
  preserve on round-trip. `Type`, `Title`, `Tags`, etc. are typed accessors
  over the same mapping other tooling reads generically.
- **Permissive-by-design loading**: `Bundle.Load` never throws on a bad
  concept file — parse failures land in `Bundle.ParseErrors` and loading
  continues, including retaining cross-link graph edges that point at
  concepts that don't exist.
- The whole thing is nullable-enabled with `TreatWarningsAsErrors`, and the
  YAML parser deliberately *rejects* anchors, tags, and multi-doc streams
  with clear errors rather than silently mishandling constructs frontmatter
  never actually uses.
- Test discipline: it's a from-scratch port of a former Rust implementation
  in the same repo, and the migration was only considered done once CLI
  output matched the Rust binary byte-for-byte on golden fixtures (LF
  endings, significant trailing whitespace, the works).

Article: https://dev.to/REPLACE-WITH-ARTICLE-URL
Repo (MIT-adjacent-but-actually-LGPL-3.0-or-later): https://github.com/jchable/okf4net

Curious what this sub thinks of the order-preserving-mapping-over-DTO
choice in particular — it's the one I keep going back and forth on.
```

---

## 3. LinkedIn — J4

**Destination:** personal LinkedIn post.
**Link:** the ARTICLE — `https://dev.to/REPLACE-WITH-ARTICLE-URL`

```
Why I spent my spare evenings porting a Google spec to C#.

Open Knowledge Format (OKF) is a small, elegant idea: store knowledge as
plain markdown files with YAML frontmatter, cross-linked like a wiki, in a
git repo. No database, no vendor lock-in — if you can `git clone` a repo,
you can read and ship its knowledge.

I wanted that for .NET too — fitting .NET's own constraints: no
third-party runtime dependencies, and a CLI that runs anywhere without
installing a runtime first. That became OKF4net: a from-scratch C#
implementation, BCL-only, publishing as a self-contained Native AOT
binary.

The part I'm most excited about isn't the file format itself — it's what
it unlocks for AI agents. Markdown-in-git is a memory substrate an agent
can read, cite, and write back to, reviewable in a normal diff.
OKF4net.Agents plugs bundles straight into the Microsoft Agent Framework
as tools an agent can call directly.

It's a young, open project (LGPL-3.0-or-later) and welcoming to first
contributions. If knowledge management, .NET internals, or agent tooling
is your thing, I'd love your eyes on it.

Full story: https://dev.to/REPLACE-WITH-ARTICLE-URL
Repo: https://github.com/jchable/okf4net

#dotnet #csharp #opensource #AIagents #softwareengineering
```

(Word count: ~181 words excluding hashtags/links, within the 120–200 target.)

---

## 4. Micro-blog thread — Bluesky / Mastodon — J4

**Destination:** Bluesky and/or Mastodon, posted as a linked reply-thread.
**Format note:** identical thread works on both platforms; Mastodon's limit
is platform-configurable but usually ≥500 chars, so the Bluesky ~300-char
cap below is the binding constraint for both. Character counts below are
raw character counts of the post text (link included), measured with the
text as written — comfortably under 300 even if a given client counts the
🧵 emoji as two characters.

**Post 1/5 — the hook** (267 chars)
```
OKF stores knowledge as plain markdown files with YAML frontmatter in a git
repo. No database, no vector store required. I just shipped OKF4net: a
zero-dependency .NET port, Native AOT CLI, and an agent-tooling layer. If
you can `git clone`, you can ship knowledge. 🧵
```

**Post 2/5** (271 chars)
```
The core library (OKF4net) has zero third-party runtime deps — just the
.NET BCL. It ships its own YAML-subset parser, markdown link scanner, and
CLI. `okf validate|info|index|graph|parse|fmt` builds to one Native AOT
binary, no runtime install needed on target machines.
```

**Post 3/5** (268 chars)
```
OKF4net.Agents wires bundles into Microsoft Agent Framework: 9 function
tools (read, browse, graph, search, write, validate...) plus
OkfContextProvider, which auto-injects bundle context and can capture agent
memory as git-native markdown, diffable and human-readable.
```

**Post 4/5** (265 chars)
```
Ported from this repo's former Rust implementation, proven byte-exact:
full test suite passing end-to-end, incl. golden CLI comparisons, before
the Rust code was retired. LGPL-3.0-or-later, independent from and not
affiliated with Google (who defined the OKF spec).
```

**Post 5/5 — the CTA** (257 chars)
```
It's young and the roadmap is intentionally beginner-friendly: good-first-
issue label, no prior OKF knowledge needed. If zero-dep .NET or git-native
agent memory interests you, I'd love a hand.
https://github.com/jchable/okf4net/labels/good%20first%20issue
```

---

## 5. Agents-angle post (dev.to note or micro-blog) — J5

**Destination:** short dev.to note, or a single longer micro-blog post
(Mastodon's higher character limit suits this better than Bluesky's 300).
**Link:** OKF4net.Agents section of the repo README —
`https://github.com/jchable/okf4net#using-okf4net-with-microsoft-agent-framework`
(secondary link, if the platform allows two: the repo root,
`https://github.com/jchable/okf4net`)

### Title (dev.to note)

```
Git-native, human-readable agent memory: what OKF4net.Agents actually stores on disk
```

### Body

```
Most "agent memory" is a vector store you can't `cat`. OKF4net.Agents takes
a different bet: agent memory as plain markdown files with YAML
frontmatter, in the same git-versioned bundle the agent already reads from
and writes to.

OKF4net is a zero-dependency .NET port of Google's Open Knowledge Format —
knowledge as a directory of cross-linked markdown concepts. OKF4net.Agents
layers the Microsoft Agent Framework on top: `OkfBundleTools` exposes nine
function tools (read, browse, graph, search, write, append-log, regenerate
indexes, validate, changes-since), and `OkfContextProvider` is where the
"git-native memory" part lives.

Register the provider alongside the tools and, opt-in via
`MemoryCaptureMode.SharedBundle`, every exchange is captured deterministically
— no extra LLM call — into one memory concept per UTC day, plus a matching
`log.md` entry. The captured text is blockquote-neutralized so a
prompt-injection payload in a concept body can't fake document structure.
The result is memory you can open in a text editor, diff, `grep`, review in
a pull request, or roll back with `git revert` — the same affordances you
already trust for code.

It's v1 and deliberately narrow about it: memory is bundle-global and
unscoped (no session/user/tenant key), which is exactly why capture defaults
to disabled — it's meant for a bundle you intend as shared, non-sensitive
memory. Scoped, multi-tenant memory tiers are sketched in the repo's design
notes but not implemented.

If "agent memory as git-diffable files" is a model you'd want to build on
top of, the code and the caveats are both in the README:
https://github.com/jchable/okf4net#using-okf4net-with-microsoft-agent-framework
```
