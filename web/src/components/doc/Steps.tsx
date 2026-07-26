// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface StepsProps {
  /** `<li>` elements, one per step. */
  children: ReactNode
}

/**
 * A numbered process — CSS counters draw the badge, so callers just supply
 * plain `<li>` elements.
 * Port of `ol.steps` — see the specimen source in
 * `design-system/styleguide.html:240-248` and usage in the former
 * `website/docs/mcp.html` "Connect Claude Desktop" chapter.
 */
export default function Steps({ children }: StepsProps) {
  return <ol className="steps">{children}</ol>
}
