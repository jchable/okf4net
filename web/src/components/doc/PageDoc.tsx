// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface PageDocProps {
  /** File path shown in the chrome bar, e.g. `my_bundle/<b>what-okf-is.md</b>`. */
  path: ReactNode
  /** Concept `type:` value, e.g. `Guide`. */
  type: ReactNode
  /** Rendered `<h1>` — may contain inline markup (e.g. `<em>`). */
  title: ReactNode
  /** Rendered lede paragraph — may contain inline markup (links, `<strong>`, `<code>`). */
  lede: ReactNode
}

/**
 * The `.page-doc` subpage opener: a slimmer document window than the home
 * hero — chrome bar (path + type) followed by an `<h1>` and a `.lede`
 * paragraph, no raw/rendered toggle.
 * Port of `website/what-okf-is.html:33-44`.
 */
export default function PageDoc({ path, type, title, lede }: PageDocProps) {
  return (
    <div className="page-doc">
      <div className="doc-window">
        <div className="doc-chrome">
          <span className="path">{path}</span>
          <span>type: {type}</span>
        </div>
        <div className="rendered">
          <h1>{title}</h1>
          <p className="lede">{lede}</p>
        </div>
      </div>
    </div>
  )
}
