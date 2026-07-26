// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface ConceptGridProps {
  children: ReactNode
}

/**
 * The 2-up bordered grid of defined terms.
 * Port of `.concept-grid` — see the specimen source in
 * `design-system/styleguide.html:197-202` and usage in
 * `website/what-okf-is.html:50-67`.
 */
export default function ConceptGrid({ children }: ConceptGridProps) {
  return <div className="concept-grid">{children}</div>
}

export interface CellProps {
  children: ReactNode
}

/** One `.concept-grid` cell — pairs a `Term` with its definition paragraph(s). */
export function Cell({ children }: CellProps) {
  return <div className="cell">{children}</div>
}

export interface TermProps {
  children: ReactNode
}

/** The bold mono term label inside a `Cell`. */
export function Term({ children }: TermProps) {
  return <div className="term">{children}</div>
}
