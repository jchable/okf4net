// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface CtaProps {
  /** The `<h2>` headline, e.g. `"Ship knowledge as files."`. */
  title: ReactNode
  /** Body content — typically a `<p>` followed by a `.hero-actions` div of `.btn` links. */
  children: ReactNode
}

/**
 * The closing call-to-action panel — soft-blue background, blue top rule.
 * Port of `.cta` — see the specimen source in
 * `design-system/styleguide.html:282-291` and usage in
 * `website/index.html`'s "Contributing" chapter.
 */
export default function Cta({ title, children }: CtaProps) {
  return (
    <div className="cta">
      <h2>{title}</h2>
      {children}
    </div>
  )
}
