// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface CodeBlockProps {
  /**
   * Pre-highlighted content, authored literally (whitespace is significant —
   * blank lines and indentation are part of the displayed code). Callers
   * pass syntax spans (`<span className="k">`, `.s`, `.c`, `.ok`) the same
   * way the static pages hand-wrote them.
   */
  children: ReactNode
}

/**
 * The ink code panel with the blue left rule.
 * Port of `pre.block` — see the specimen source in
 * `design-system/styleguide.html:206-215` and usage in `website/index.html`
 * (e.g. the `okf validate` / C# samples).
 */
export default function CodeBlock({ children }: CodeBlockProps) {
  return <pre className="block">{children}</pre>
}
