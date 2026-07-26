// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface TagProps {
  /** Usually the literal text `"soon"`. */
  children: ReactNode
}

/**
 * The small uppercase status chip reused across the docs tree, index table,
 * and concept grids.
 * Port of `.tag` — see `website/assets/site.css` ("small 'soon' / status
 * chip, reused across the tree, index and grids") and usage e.g. in the
 * former `website/docs/index.html`'s "agents" row.
 */
export default function Tag({ children }: TagProps) {
  return <span className="tag">{children}</span>
}
