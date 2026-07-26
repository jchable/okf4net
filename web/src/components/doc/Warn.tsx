// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface WarnProps {
  /** The `.t` label, e.g. `"GOLDEN FIXTURES"`. */
  title: ReactNode
  children: ReactNode
}

/**
 * A rule that must not be broken — ink border, blue left rule.
 * Port of `.warn` — see the specimen source in
 * `design-system/styleguide.html:257-262` and usage in the former
 * `website/docs/mcp.html` ("IT WRITES TO YOUR BUNDLE").
 */
export default function Warn({ title, children }: WarnProps) {
  return (
    <div className="warn">
      <div className="t">{title}</div>
      {children}
    </div>
  )
}
