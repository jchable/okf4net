# Second brain sample

A personal-notes OKF v0.2 bundle that Claude Desktop reads **and writes**
through [`okf-mcp`](../../src/OKF4net.Mcp/README.md), the local MCP server
of this repository. You talk to Claude; Claude searches your notes, creates
new ones, appends to existing ones, regenerates the `index.md` listings (§8)
and records what changed in `log.md` (§9).

There is no program to build here. The sample is three things:

- [`bundle/`](bundle/): a small starter bundle to copy and make your own
  (projects, areas, people, notes), including a
  [note that tells Claude how to use it](bundle/notes/how-this-brain-works.md);
- this README: the Claude Desktop configuration for Windows and macOS, and
  example prompts;
- [`smoke.cs`](smoke.cs): a .NET 10 file-based app that drives `okf-mcp`
  over stdio exactly as Claude Desktop does, with no Claude Desktop, and
  checks the result on disk. Its real output is [below](#smoke-test).

Standalone like the other samples: not part of `OKF4net.sln`, not run by CI.

## What `okf-mcp` does when it is writable

`okf-mcp` is read-only by default. With `OKF_MCP_WRITABLE=1` it registers
twelve tools: the eight read tools plus `okf_write_concept`,
`okf_append_log`, `okf_regenerate_indexes` and `okf_verify`. What each write
does, checked against the code and by the smoke test below:

| Action | Tool | What happens on disk |
|--------|------|----------------------|
| Create a note | `okf_write_concept` | Writes `<id>.md`. Refused unless this server process has **read the id first** with `okf_read_concept` and been told it does not exist. |
| Change or append to a note | `okf_write_concept` | Replaces the **whole** document (frontmatter and body). Refused unless the server process has read the concept with `okf_read_concept`, and refused if the file changed since that read. After a successful write, the next write needs a new read. There is no "append a line" tool: appending is read, add the line, write it all back. |
| Provenance | (any `okf_write_concept`) | When the frontmatter you send has no `generated` key, the server adds `generated: { by: okf4net/0.2, at: <now> }`. When it has one, it is kept as sent. |
| Update the listings | `okf_regenerate_indexes` | Rewrites every `index.md` from the concepts on disk. |
| Record the change | `okf_append_log` | Adds `* **<kind>**: <text>` under today's date in the root `log.md`, then rewrites the whole file through the §9 model. |
| Review stamp | `okf_verify` | Adds a `{ by, at }` entry to a concept's `verified` list (§5.2). |

**`index.md` and `log.md` are not updated by `okf_write_concept`.** A write
changes the concept file and nothing else; the smoke test checks that both
are byte-identical after two writes. They change only when Claude calls
`okf_regenerate_indexes` and `okf_append_log`. The write tool's reply says
`Remember to run okf_regenerate_indexes`, and the bundle's own
[guide note](bundle/notes/how-this-brain-works.md) asks Claude to do both
after a batch of changes. Claude usually does when asked, but nothing
enforces it. Until it does, `okf_read_concept` and `okf_search` already see
the new note, because they read the concepts and not the indexes, while
`okf_browse` (which reads the indexes) and `okf_changes_since` (which reads
`log.md`) do not.

Three more things to know:

- **"Read first" is per server process, not per chat.** The record of what
  was read lives in the `okf-mcp` process. Claude Desktop starts that
  process when it starts, so a concept read in one conversation also counts
  for a write in another one, until Claude Desktop restarts. The record keeps
  the latest 10,000 ids.
- **`okf_append_log` rewrites `log.md`.** Anything the §9 model does not hold
  is dropped: frontmatter, prose between entries, comments. The starter
  `log.md` is already in that form, so an append adds only the new entry.
- **`okf_read_concept` flattens nested frontmatter**
  ([#256](https://github.com/jchable/okf4net/issues/256)). It shows
  `generated: { by: human:me, at: … }` as `generated: by: human:me at: …`,
  and the `verified` list `okf_verify` adds as `verified: [by: human:me at: …]`.
  If Claude copies such a line back into a write, it is stored as a plain
  string and the note loses its provenance or its review stamps. `okf validate`
  does not warn about it. The guide note tells Claude to leave `generated` out
  (the server then stamps a new one), to write every other structured field
  back as valid YAML, e.g. `verified: [{ by: human:me, at: … }]`, and to
  refuse the rewrite when it cannot rebuild such a field from what it read.
  This is an instruction to the model, not a guarantee: after a rewrite of a
  verified note, check its `verified` list.

Claude Desktop asks for permission before running a tool, unless you have
allowed it. `okf-mcp` itself never asks.

## Set it up

### 1. Copy the starter bundle

Copy `bundle/` somewhere that is yours. Claude will write to it, so do not
point Claude Desktop at the copy inside this repository.

```powershell
# Windows (PowerShell)
Copy-Item -Recurse samples\second-brain\bundle $HOME\second-brain
```

```sh
# macOS
cp -R samples/second-brain/bundle ~/second-brain
```

### 2. Choose how to run `okf-mcp`

**As the installed dotnet tool** (needs the .NET 10 runtime):

```sh
dotnet tool install -g OKF4net.Mcp
```

This puts `okf-mcp` in `~/.dotnet/tools` (`%USERPROFILE%\.dotnet\tools` on
Windows).

**From this repository**, for unreleased changes. Build once:

```sh
dotnet build src/OKF4net.Mcp
```

Then Claude Desktop starts it with `dotnet run --no-build`. Do not leave out
`--no-build`. A cold `dotnet run` builds for a minute or more before it
answers, which can be longer than Claude Desktop waits. Run
`dotnet build src/OKF4net.Mcp` again after you pull or change the code.

### 3. Edit `claude_desktop_config.json`

In Claude Desktop, open **Settings > Developer > Edit Config**, or open the
file directly:

- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`

Claude Desktop does not expand `~` or `%USERPROFILE%`. Write full paths, and
replace `you` and the repository location with yours. Both the bundle root
argument and `OKF_MCP_WRITABLE` are required: without the argument the server
finds no bundle, because Claude Desktop starts servers in an unrelated working
directory, and without `OKF_MCP_WRITABLE` it is read-only.

**Windows, installed tool:**

```json
{
  "mcpServers": {
    "second-brain": {
      "command": "C:\\Users\\you\\.dotnet\\tools\\okf-mcp.exe",
      "args": ["C:\\Users\\you\\second-brain"],
      "env": { "OKF_MCP_WRITABLE": "1" }
    }
  }
}
```

**Windows, from the repository:**

```json
{
  "mcpServers": {
    "second-brain": {
      "command": "dotnet",
      "args": [
        "run", "--no-build",
        "--project", "C:\\src\\okf4net\\src\\OKF4net.Mcp",
        "--", "C:\\Users\\you\\second-brain"
      ],
      "env": { "OKF_MCP_WRITABLE": "1" }
    }
  }
}
```

**macOS, installed tool:**

```json
{
  "mcpServers": {
    "second-brain": {
      "command": "/Users/you/.dotnet/tools/okf-mcp",
      "args": ["/Users/you/second-brain"],
      "env": { "OKF_MCP_WRITABLE": "1" }
    }
  }
}
```

**macOS, from the repository:**

```json
{
  "mcpServers": {
    "second-brain": {
      "command": "/usr/local/share/dotnet/dotnet",
      "args": [
        "run", "--no-build",
        "--project", "/Users/you/src/okf4net/src/OKF4net.Mcp",
        "--", "/Users/you/second-brain"
      ],
      "env": { "OKF_MCP_WRITABLE": "1" }
    }
  }
}
```

On macOS, Claude Desktop does not start servers with your shell's `PATH`, so
use the full path to `dotnet` (`which dotnet` shows it;
`/usr/local/share/dotnet/dotnet` is where the official installer puts it).
If the installed tool does not start, add `"DOTNET_ROOT":
"/usr/local/share/dotnet"` to `env`.

Restart Claude Desktop. `okf-mcp` logs to stderr only, because stdout carries
the JSON-RPC stream, and Claude Desktop keeps that stderr in its MCP logs
(`%APPDATA%\Claude\logs\mcp-server-second-brain.log` on Windows,
`~/Library/Logs/Claude/mcp-server-second-brain.log` on macOS). The first
line says `okf-mcp: serving bundle root …`, followed by `(read-only)` if
`OKF_MCP_WRITABLE` was not picked up.

## Example prompts

Start a conversation by pointing Claude at the conventions:

> Read `notes/how-this-brain-works` from my second brain and follow it for
> the rest of this conversation.

Then, for example:

- *What's on my reading list?*: `okf_search` or `okf_read_concept`.
- *Add "A Philosophy of Software Design" to my reading list, Sam recommended
  it.*: read, then `okf_write_concept` with the whole note and the new line.
- *New note: I want to try a standing desk for a month before buying one.
  Link it to my running area.*: `okf_read_concept` on the new id (not found),
  then `okf_write_concept`.
- *Sam called back: the second quote is 3,900 EUR with eight weeks' lead
  time. Update the kitchen project and Sam's note.*: two reads, two writes.
- *Regenerate the indexes and log what we changed today.*:
  `okf_regenerate_indexes`, then `okf_append_log`.
- *What changed in my notes since 2026-11-15?*: `okf_changes_since`, which
  reads `log.md`, so it shows only what was logged.
- *Which notes link to the kitchen renovation?*: `okf_graph`.
- *Check the bundle is still valid.*: `okf_validate_bundle`.
- *Mark the kitchen project as reviewed by me.*: `okf_verify` with
  `human:me`. The stamp is a dated declaration, not a proof.

## Smoke test

`smoke.cs` checks all of the above without Claude Desktop. It copies
`bundle/` to a temporary directory, starts `okf-mcp` on the copy with
`OKF_MCP_WRITABLE=1` (and without an inherited `OKF_MCP_READONLY`, which
would win), and sends it raw JSON-RPC: `initialize`, `tools/list`,
a write before any read (refused), a read of a new id followed by its
creation, and an append to the reading list (read, then write the whole
document). It then checks `index.md` and `log.md` on disk, calls
`okf_regenerate_indexes` and `okf_append_log`, closes stdin and reads stdout
to EOF, checks that every line `okf-mcp` wrote there, shutdown included, was a
JSON-RPC 2.0 message, and finally runs `okf validate` on the copy.
From the repository root:

```sh
dotnet run samples/second-brain/smoke.cs              # okf-mcp built from this repository
dotnet run samples/second-brain/smoke.cs -- --tool    # the installed okf-mcp dotnet tool
dotnet run samples/second-brain/smoke.cs -- --keep    # keep the copy to look at it
```

It exits with 0 when every check passed. `okf-mcp` returns each tool's text
as a JSON string literal (in quotes, with `\n` escapes), and the script
decodes it before printing. A real run, on 2026-11-18, of the
repository build. The `--tool` run, against a locally packed `OKF4net.Mcp`,
passed the same checks:

<details>
<summary>Output</summary>

```text
bundle copy: <temp>/okf-second-brain-1694d371
server: dotnet run --no-build --project <repo>\src\OKF4net.Mcp -- <copy>

== initialize
  server: okf 0.7.0, protocol 2025-06-18

== tools/list
  12 tools: okf_read_concept, okf_browse, okf_graph, okf_search, okf_audit, okf_write_concept, okf_verify, okf_append_log, okf_regenerate_indexes, okf_validate_bundle, okf_changes_since, okf_get_computation
  [ok] twelve tools with OKF_MCP_WRITABLE=1
  [ok] the write tools are registered

== the observation gate

> okf_write_concept {"conceptId":"notes/standing-desk-idea","frontmatterYaml":"type: Note\ntitle: Standing desk idea\ndescription: Try a standing desk for a month before buying one.\ntags: [home, health]","body":"# Idea\n\nBorrow a converter for a month, note how often I actually stand.\nRelated: [running](../areas/running.md).\n"}
    Error: 'notes/standing-desk-idea' has not been read by this session; read it first with okf_read_concept (a context block injected for you, or okf_get_computation, does not count), then write it.

  [ok] a write before any read is refused, nothing written

== create a note (read first: the read reports it absent)

> okf_read_concept {"conceptId":"notes/standing-desk-idea"}
    Concept 'notes/standing-desk-idea' not found. Use okf_browse to list available concepts.


> okf_write_concept {"conceptId":"notes/standing-desk-idea","frontmatterYaml":"type: Note\ntitle: Standing desk idea\ndescription: Try a standing desk for a month before buying one.\ntags: [home, health]","body":"# Idea\n\nBorrow a converter for a month, note how often I actually stand.\nRelated: [running](../areas/running.md).\n"}
    Written notes/standing-desk-idea (new, 306 bytes). Remember to run okf_regenerate_indexes.

  [ok] the new note exists on disk
  [ok] the server stamped `generated` (none was given)

== append to a note (read, add a line, write the whole document back)

> okf_read_concept {"conceptId":"notes/reading-list"}
    # Reading list
    
    type: Note
    title: Reading list
    description: Books and articles to read, with a line on why each one is on the list.
    tags: [reading, list]
    generated: by: human:me at: 2026-11-15T09:00:00Z
    
    ```markdown
    # To read
    
    - *The Pragmatic Programmer* — reread the chapter on tracer bullets.
    - *How to Take Smart Notes* (Sönke Ahrens) — the idea behind keeping this bundle.
    
    # Read
    
    - *Shape Up* (Basecamp) — six-week cycles; the "appetite" framing helped scope
      the [kitchen renovation](../projects/kitchen-renovation.md).
    ```
    
    ## Outgoing links
    - projects/kitchen-renovation
    
    ## Backlinks
    - notes/how-this-brain-works


> okf_write_concept {"conceptId":"notes/reading-list","frontmatterYaml":"type: Note\ntitle: Reading list\ndescription: Books and articles to read, with a line on why each one is on the list.\ntags: [reading, list]","body":"# To read\n\n- *The Pragmatic Programmer* — reread the chapter on tracer bullets.\n- *How to Take Smart Notes* (Sönke Ahrens) — the idea behind keeping this bundle.\n- *A Philosophy of Software Design* (John Ousterhout) — recommended by Sam.\n\n# Read\n\n- *Shape Up* (Basecamp) — six-week cycles; the \"appetite\" framing helped scope\n  the [kitchen renovation](../projects/kitchen-renovation.md).\n"}
    Written notes/reading-list (updated, 606 bytes). Remember to run okf_regenerate_indexes.

  [ok] the appended line is on disk
  [ok] the rewrite carries a fresh `generated` stamp

> okf_write_concept {"conceptId":"notes/reading-list","frontmatterYaml":"type: Note\ntitle: Reading list\ndescription: Books and articles to read, with a line on why each one is on the list.\ntags: [reading, list]","body":"# To read\n\n- *The Pragmatic Programmer* — reread the chapter on tracer bullets.\n- *How to Take Smart Notes* (Sönke Ahrens) — the idea behind keeping this bundle.\n- *A Philosophy of Software Design* (John Ousterhout) — recommended by Sam.\n\n# Read\n\n- *Shape Up* (Basecamp) — six-week cycles; the \"appetite\" framing helped scope\n  the [kitchen renovation](../projects/kitchen-renovation.md).\n"}
    Error: 'notes/reading-list' has not been read by this session; read it first with okf_read_concept (a context block injected for you, or okf_get_computation, does not count), then write it.

  [ok] a second write without a fresh read is refused

== index.md and log.md after the writes alone
  index.md: unchanged
  notes/index.md: unchanged
  log.md: unchanged
  [ok] okf_write_concept touches neither index.md nor log.md

== the explicit follow-up tools

> okf_regenerate_indexes {}
    Regenerated 5 index file(s):
    - areas/index.md
    - notes/index.md
    - people/index.md
    - projects/index.md
    - index.md


> okf_append_log {"kind":"Creation","text":"added the [standing desk idea](/notes/standing-desk-idea.md); one book on the [reading list](/notes/reading-list.md)."}
    Appended a 'Creation' entry under 2026-11-18 in log.md.

  [ok] notes/index.md now lists the new note
  [ok] log.md has the new entry

> okf_changes_since {"sinceDate":"2026-11-18"}
    # Changes since 2026-11-18
    
    ## log.md
    ### 2026-11-18
    - **Creation**: added the [standing desk idea](/notes/standing-desk-idea.md); one book on the [reading list](/notes/reading-list.md).


== stdio
  [ok] okf-mcp closed stdout within 30 s of stdin closing
  [ok] every line okf-mcp wrote on stdout was JSON-RPC

== log.md on disk
    # Bundle history
    
    ## 2026-11-18
    * **Creation**: added the [standing desk idea](/notes/standing-desk-idea.md); one book on the [reading list](/notes/reading-list.md).
    
    ## 2026-11-16
    * **Creation**: started the [kitchen renovation](/projects/kitchen-renovation.md) project and a note on [Sam Rivera](/people/sam-rivera.md).
    
    ## 2026-11-15
    * **Initialization**: created the bundle with a [guide to its conventions](/notes/how-this-brain-works.md), a [reading list](/notes/reading-list.md) and the [running](/areas/running.md) area.

== okf validate
    [warning] <copy>\areas\running.md: missing recommended frontmatter field `resource`
    [warning] <copy>\notes\how-this-brain-works.md: missing recommended frontmatter field `resource`
    [warning] <copy>\notes\reading-list.md: missing recommended frontmatter field `resource`
    [warning] <copy>\notes\standing-desk-idea.md: missing recommended frontmatter field `resource`
    [warning] <copy>\people\sam-rivera.md: missing recommended frontmatter field `resource`
    [warning] <copy>\projects\kitchen-renovation.md: missing recommended frontmatter field `resource`
    
    6 concept(s); 0 error(s), 6 warning(s), 0 info.
    ✓ conformant with OKF v0.2

  [ok] okf validate exit code 0

ALL CHECKS PASSED
```

</details>

## Validating the bundle

```sh
dotnet run --project src/OKF4net.Cli -- validate samples/second-brain/bundle
```

The bundle is conformant, with 0 errors and exit code 0. It shows one
warning per note, `missing recommended frontmatter field resource`, and
these are expected. §4.1 recommends `resource` but says it is "absent for
concepts that describe abstract ideas rather than physical resources", and a
personal note is one. The validator exempts only Attested Computations from
that warning (see S4.1-8 in
[the spec gap report](../../docs/spec-conformance/2026-07-31-okf-spec-gap-report.md)),
so every note Claude adds will carry the same warning.
