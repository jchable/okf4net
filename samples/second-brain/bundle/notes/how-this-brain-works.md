---
type: Guide
title: How this second brain works
description: The conventions an assistant follows when it reads and writes notes in this bundle.
tags: [meta, conventions]
generated: { by: human:me, at: 2026-11-15T09:00:00Z }
---

# What lives where

| Directory    | Holds                                         | `type`    |
|--------------|-----------------------------------------------|-----------|
| `notes/`     | Free-standing notes, lists, ideas             | `Note`    |
| `projects/`  | Things with an end date and a next action     | `Project` |
| `areas/`     | Ongoing responsibilities with no end date     | `Area`    |
| `people/`    | People I deal with, and what I owe them       | `Person`  |

A concept id is its path without `.md`: `projects/kitchen-renovation`.
Ids are lowercase, words separated by hyphens.

# Writing a note

1. Search first (`okf_search`) — the note may already exist.
2. To change an existing note, read it with `okf_read_concept` in the same
   conversation, then write the **whole** document back with
   `okf_write_concept`: frontmatter and body. There is no "append a line" tool;
   appending is read, add, write.
3. To create a note, read its id first too: the read answers "not found",
   and that is what allows the creation.
4. Every note needs `type`, `title` and a one-sentence `description`.
5. `okf_read_concept` shows the frontmatter as a fenced `yaml` block. When
   you rewrite a note, copy that block back as shown, changing only the
   fields you mean to change. Keep `generated` and `verified` exactly as they
   are: they record who wrote and who reviewed the note. On a new note, leave
   `generated` out and the server stamps one.
6. Link related notes with relative markdown links, e.g.
   `[Sam Rivera](../people/sam-rivera.md)`.

# After writing

Writing a note does not touch `index.md` or `log.md`. After a batch of
changes:

- call `okf_regenerate_indexes` so the directory listings include the new
  notes;
- call `okf_append_log` with kind `Creation` or `Update` and one line saying
  what changed.

See [the reading list](reading-list.md) for an example of a note that grows
over time.
