// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import { docsTree } from '../content/docs'
import { Tag } from './doc'

export interface DocsSidebarProps {
  /** `slug` of the docs page currently being rendered — gets `.current`. */
  current: string
}

/**
 * The docs section sidebar: an ASCII tree (`├`/`└`) of the `docs/` bundle,
 * generated from the `docsTree` manifest rather than hand-copied per page.
 * Port of `aside.docs-side` (`website/docs/index.html:35-52`, commit
 * `40fe17f`).
 */
export default function DocsSidebar({ current }: DocsSidebarProps) {
  return (
    <aside className="docs-side" aria-label="Documentation">
      <div className="side-h">docs bundle</div>
      <ul className="tree">
        <li className="root">docs/</li>
        {docsTree.map((entry, i) => {
          const branch = i === docsTree.length - 1 ? '└ ' : '├ '

          if (entry.soon) {
            return (
              <li key={entry.slug} className="soon">
                <span className="b">{branch}</span>
                <span className="name">{entry.label}</span>
                <Tag>soon</Tag>
              </li>
            )
          }

          const to = entry.slug === 'index' ? '/docs' : `/docs/${entry.slug}`
          return (
            <li key={entry.slug} className={entry.slug === current ? 'current' : undefined}>
              <span className="b">{branch}</span>
              <Link to={to}>{entry.label}</Link>
            </li>
          )
        })}
      </ul>
      <div className="side-foot">
        This tree is a real bundle.
        <br />
        <code>okf browse docs/</code> would print the same listing.
      </div>
    </aside>
  )
}
