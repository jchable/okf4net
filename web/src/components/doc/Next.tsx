// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface NextProps {
  /** Usually `→ <Link>concept.md</Link> — teaser text`. */
  children: ReactNode
}

/**
 * The cross-link teaser closing a chapter.
 * Port of `p.next` — see the specimen source in
 * `design-system/styleguide.html:275-280` and usage throughout
 * `website/index.html` and `website/what-okf-is.html`.
 */
export default function Next({ children }: NextProps) {
  return <p className="next">{children}</p>
}
