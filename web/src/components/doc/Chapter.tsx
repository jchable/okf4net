// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface ChapterProps {
  /** Anchor id for the `<section>`, e.g. `"terms"`. */
  id: string
  /** Chapter heading — may contain inline markup. */
  title: ReactNode
  /** Spec-reference annotation shown at the right of the chapter head, e.g. `"§2–§3 — bundle, concept, id"`. */
  refText: ReactNode
  children: ReactNode
}

/**
 * A document chapter: `section.chapter` opened by a `.chead` (the `##`
 * markdown-heading badge, an `<h2>`, and a `.ref` spec citation), followed by
 * the chapter body.
 * Port of `design-system/styleguide.html:182-186` (`.chead` specimen source)
 * and e.g. `website/what-okf-is.html:48-49`.
 */
export default function Chapter({ id, title, refText, children }: ChapterProps) {
  return (
    <section className="chapter" id={id}>
      <div className="chead">
        <span className="h">##</span>
        <h2>{title}</h2>
        <span className="ref">{refText}</span>
      </div>
      {children}
    </section>
  )
}
