### For: `GoogleCloudPlatform/knowledge-catalog` (upstream OKF spec), not this repo's own tracker

**Suggested title:** Clarify allowed character set for concept ids (§2) — ASCII-safe subset vs. full Unicode?

**Suggested labels (upstream repo's own labels, adjust to match):** spec / question / clarification

---

**Context**

I maintain [OKF4net](https://github.com/jchable/okf4net), an independent .NET implementation of OKF v0.2. While designing an API surface for programmatically constructing concept ids (e.g. deriving one from a free-form title), I went looking for what SPEC.md says about which characters are valid in a concept id / file path segment, and found nothing:

> §2 defines: "**Concept ID**: The path of the concept's file within the bundle, with the `.md` suffix removed." — this describes *what* a concept id is, not what characters it may contain. I could not find a grammar, regex, whitelist, or blacklist anywhere else in SPEC.md either; §3's bundle-structure examples only show `<concept>.md` placeholders, and §3.1 only reserves the two literal names `index.md`/`log.md`.

That silence seems intentional given §1's "intentionally minimal" / "minimally opinionated" framing — but it leaves a real interoperability question open for independent implementations.

**The concrete problem**

OKF4net's own `ConceptId` validator currently restricts segments to `[A-Za-z0-9_][A-Za-z0-9_.\-]*` (ASCII letters/digits/`_`/`.`/`-`, first character excluding `.`/`-`) — stricter than the spec requires, apparently chosen to mirror another reference implementation's behavior rather than derived from spec text. Before locking in more API around this rule (e.g. a "derive a concept id from an arbitrary title" helper), I'd like to know whether that ASCII restriction should really be an implementation-local choice, or whether the spec should say something explicit — because there's a real interoperability hazard either way:

- If implementations are free to allow full Unicode in concept ids (any alphabet, no transliteration), two independent, spec-conformant implementations can disagree on whether a given bundle is valid, and a bundle built by one may be rejected — or silently mishandled — by another.
- Filesystem round-tripping compounds this: macOS (HFS+/APFS in its default mode) normalizes filenames to NFD (decomposed), while Windows and most Linux filesystems preserve whatever normalization form was written (commonly NFC). A concept id containing a precomposed accented character (e.g. "café") can therefore round-trip differently depending on which OS wrote/read the bundle, which breaks concept-id equality/lookup for any implementation that compares segments byte-for-byte (ordinal) rather than normalizing first.

**What would help**

Some explicit guidance in §2 (even a non-normative note) on one of:
1. A recommended/required character set for concept id segments (e.g. an ASCII-safe subset, for maximum cross-implementation and cross-filesystem portability), or
2. If full Unicode is intended to be allowed, a mandated normalization form (e.g. "producers MUST write NFC-normalized segments; consumers SHOULD normalize before comparing") to close the cross-platform round-trip hazard above, or
3. An explicit statement that this is deliberately left to implementations/producers, if that's the intended answer — so implementers stop guessing and independently converging on different rules.

Happy to contribute a PR to SPEC.md once there's a direction, and to share what OKF4net currently does (and why) if useful as one data point.
