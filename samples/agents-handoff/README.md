# Agents hand-off sample

Two [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
agents hand knowledge to each other through one OKF bundle, with
[OKF4net.Agents](../../src/OKF4net.Agents/README.md): a **curator** that
writes a note with the read-write tools, and a **reader** — read-only tools
plus an `OkfContextProvider` — that finds the note in its injected context in
a later, separate conversation. Like
[`samples/agents-quickstart/`](../agents-quickstart/README.md), it runs with
**no LLM endpoint, no API key and no network access**: each "model" is a small
scripted `IChatClient`; the tools, the context provider and the Agent
Framework's tool-calling pipeline are the real ones.

What it shows that the other two agent samples do not:

- **One `BundleHandle` shared by two tool sets and a context provider**
  (spec §5.2), so a write committed through one is what the others read next —
  even under the `Manual` refresh policy, because a write through a handle
  invalidates it whatever the policy.
- **`OkfToolMode.ReadWrite` next to `OkfToolMode.ReadOnly`** over that same
  handle: one agent may write, the other cannot even see a write tool.
- **The write gate of `okf_write_concept`** (spec §5.4): a session can only
  write a concept it has read with `okf_read_concept`, or seen absent there.
- **Consumers outside the shared handle**: one built from a root string
  (its own `Manual` handle, which keeps its old image until refreshed) and one
  over its own `OnAccess` handle (which notices the change by itself).

Standalone: this project has its own `AgentsHandoff.sln`, is not part of
`OKF4net.sln`, and is not built or tested by this repo's CI. Its only
reference is a `ProjectReference` to `src/OKF4net.Agents`; `Microsoft.Agents.AI`
and the `OKF4net` core come through it, so the project has no
`PackageReference` of its own.

## Run

```bash
dotnet run --project samples/agents-handoff/src/AgentsHandoff
```

The curator writes, so the sample never works on the repository: it copies
[`bundles/ga4`](../../bundles/README.md#ga4) to a new directory under the
system temp directory, runs everything there, and deletes it at exit. Pass
`--keep` (`dotnet run --project … -- --keep`) to keep the copy and print its
path, to look at the note and the regenerated indexes afterwards. Running the
sample leaves `git status` clean.

Optional: `OKF_BUNDLE_ROOT` points it at another bundle to copy (a relative
value resolves against the current directory). The copy takes ordinary files
and directories only and skips links (symlinks, junctions), so nothing outside
that bundle reaches the agents. The note's links and the
scripted answers are written for `ga4`: on another bundle the note is still
written and picked up, but its two links point at concepts that may not exist.

## What it does

1. **One handle, two tool sets.** The host builds one `BundleHandle` over a
   `FileSystemBundleProvider` (refresh policy `Manual`) and two
   `OkfBundleTools` over it: the curator's, whose `GetTools(OkfToolMode.ReadWrite)`
   offers all twelve tools, and the reader's, whose `GetTools(OkfToolMode.ReadOnly)`
   offers eight, with an `OkfContextProvider` built over the reader's tool set.
   One tool set per agent, because what `okf_read_concept` showed a session is
   recorded per tool set; one handle for both, because the image is the
   handle's. Two more consumers of the same directory load their own image
   before anything is written: `new OkfBundleTools(root)` (its own `Manual`
   handle) and a tool set over its own `OnAccess` handle.
2. **The curator writes a note.** Its scripted model calls
   `okf_write_concept` for `notes/purchasers-on-the-sample-dataset`, which is
   refused because the session has not read it; it calls `okf_read_concept`
   (not found — recorded as seen absent), writes the note (a `MustNotExist`
   write, conditioned on what it saw), and calls `okf_regenerate_indexes` so
   the new `notes/` directory is listed. The note says the purchasers query's
   `_TABLE_SUFFIX` range is a template, to narrow on the sample dataset.
3. **The reader picks it up.** A separate agent, conversation and tool set.
   Before its first model turn the `OkfContextProvider` injects the root
   listing and the concepts that score best against the question, read from the
   shared handle: the curator's write invalidated it, so the note is the first
   concept injected, with no `Refresh` call anywhere. The scripted model then
   opens the note with `okf_read_concept` and answers.
4. **The consumers outside the shared handle.** The `OnAccess` one sees the
   note at its next use, because the provider's version token changed. The one
   built from a root string still answers "not found" from the image it loaded
   in step 1, until the host calls `Handle.RefreshAsync()`.

Regenerating the indexes rewrites them from the bundle's concepts (§8), so in
the copy the two directory descriptions upstream's generator had written for
`references/` and `references/metrics/` become `Contains 7: …` listings.

## Output

A real run (the `generated` timestamp the curator's write stamps into the
note is the only thing that changes between runs, and it is not printed):

```text
OKF4net.Agents hand-off sample
No LLM endpoint, no API key, no network: both "models" below are scripted stand-ins.
Bundle: a temporary working copy of bundles/ga4 (the repository is never written).

=== 1. One BundleHandle, two agents' tool sets ===
The host builds ONE BundleHandle over the working copy and gives it to two OkfBundleTools:
a curator's, offered every tool (OkfToolMode.ReadWrite), and a reader's, offered the
read-only ones (OkfToolMode.ReadOnly) plus an OkfContextProvider built over them. One tool
set per agent, because what okf_read_concept showed a session is recorded per tool set;
one handle for both, because the image of the bundle is the handle's.
Its refresh policy is Manual -- nothing reloads it on a timer or on access -- yet a write by
a tool set built over it invalidates it whatever the policy (spec §5.1, §5.2).

  curator: 12 tools, including okf_write_concept, okf_verify, okf_append_log, okf_regenerate_indexes
  reader:  8 tools, none of them a write tool, plus the OkfContextProvider

Two more consumers of the same directory, for comparison. Neither is given the shared
handle: `new OkfBundleTools(root)` builds its own (Manual), and the second is built over
a handle of its own with the OnAccess policy, which compares the provider's version
token before every use. Both load their image now, before anything is written:

  outsider (own handle, Manual):   Concept 'notes/purchasers-on-the-sample-dataset' not found. Use okf_browse to list available concepts.
  watcher  (own handle, OnAccess): Concept 'notes/purchasers-on-the-sample-dataset' not found. Use okf_browse to list available concepts.

=== 2. The curator agent writes a note ===
The curator's scripted model tries to write the note straight away, which okf_write_concept
refuses: a session may only write a concept it has read with okf_read_concept (or seen
absent there), so that a write is conditioned on the version it saw (spec §5.4). It reads,
is told the concept does not exist, writes it, and regenerates the indexes so the new
notes/ directory is listed.

  user: Leave a note for the analysts: the purchasers query's date range needs narrowing on the sample dataset.
  [curator, turn 1] received the user's question (12 tools offered)
  [curator, turn 1] replies with a call to okf_write_concept
  [curator, turn 2] received a tool result (209 chars): Error: 'notes/purchasers-on-the-sample-dataset' has not been read by this session; read it first with okf_r...
  [curator, turn 2] replies with a call to okf_read_concept
  [curator, turn 3] received a tool result (102 chars): Concept 'notes/purchasers-on-the-sample-dataset' not found. Use okf_browse to list available concepts.
  [curator, turn 3] replies with a call to okf_write_concept
  [curator, turn 4] received a tool result (104 chars): Written notes/purchasers-on-the-sample-dataset (new, 680 bytes). Remember to run okf_regenerate_indexes.
  [curator, turn 4] replies with a call to okf_regenerate_indexes
  [curator, turn 5] received a tool result (147 chars): Regenerated 6 index file(s):
  [curator, turn 5] replies with its final answer
  [tools called: okf_write_concept, okf_read_concept, okf_write_concept, okf_regenerate_indexes]
  assistant: Left the note for the analysts at notes/purchasers-on-the-sample-dataset, and regenerated the indexes.

=== 3. The reader agent picks the note up ===
A different agent, a different conversation, read-only tools. Its OkfContextProvider runs
before the model's first turn and injects the bundle's root listing plus the concepts that
score best against the question -- read from the shared handle, which the curator's write
invalidated, so the note is among them without anyone calling Refresh. The scripted model
then opens the note with okf_read_concept, also served from the shared image.

  user: How do I count purchasers on the GA4 sample dataset?
  [reader, turn 1] received the user's question (8 tools offered)
  [reader, turn 1] injected context blocks: index, notes/purchasers-on-the-sample-dataset, datasets/ga4_obfuscated_sample_ecommerce, references/metrics/purchasers, tables/events_, references/metrics/frequently_active_users
  [reader, turn 1] replies with a call to okf_read_concept
  [reader, turn 2] received a tool result (840 chars): # Counting purchasers on the GA4 sample dataset
  [reader, turn 2] replies with its final answer
  [tools called: okf_read_concept]
  assistant: Run the purchasers query (references/metrics/purchasers), but on the sample dataset narrow _TABLE_SUFFIX to the November 2020 - January 2021 shards (source: notes/purchasers-on-the-sample-dataset).

=== 4. What the consumers outside the shared handle see ===
The write invalidated the shared handle only. The watcher's OnAccess policy notices the
directory changed at its next use; the outsider keeps serving the image it loaded in
step 1 until its host refreshes it.

  watcher  (OnAccess):                # Counting purchasers on the GA4 sample dataset
  outsider (Manual):                  Concept 'notes/purchasers-on-the-sample-dataset' not found. Use okf_browse to list available concepts.
  outsider after Handle.RefreshAsync: # Counting purchasers on the GA4 sample dataset

Share one handle between every consumer that must agree on the bundle; pick a refresh policy
for the changes that come from elsewhere (another process, a git pull, a human editor).
```

## What the scripts cannot show

Both final answers, and the curator's whole sequence of calls, are part of the
scripts; a real model would decide them from the tool results. In particular a
real curator meets the write gate the same way: the refusal text tells it to
read first, and a context block injected by an `OkfContextProvider` does not
count as a read (an injection is never an observation).

## Using a real model

Assign a real `IChatClient` to `curatorModel` and `readerModel` in
`Program.cs` instead of the `ScriptedChatClient`s; the `AsAIAgent` calls, the
tool lists, the context provider and the shared handle stay as they are. That
needs a provider package (for example `Microsoft.Extensions.AI.OpenAI`), which
this sample deliberately does not carry —
[`samples/acme-retail-agent/`](../acme-retail-agent/README.md) shows one wired
to any OpenAI-compatible endpoint. With a real curator, consider
`OkfToolMode.RequireApprovalForWrites` instead of `ReadWrite`, so that a human
approves each write: bundle content is untrusted, and an ungated write tool is
how a prompt injection carried in a concept becomes a persistent change.

Memory capture (`OkfContextProviderOptions.MemoryCapture`) is left off here,
as it is by default: this sample hands off curated concepts, not conversation
transcripts.
