// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'

/** Site navigation keys, one per top-level chapter of the OKF4net bundle. */
export type NavKey = 'home' | 'what-okf-is' | 'library' | 'cli' | 'docs' | 'contributing'

interface NavItem {
  key: NavKey
  to: string
  label: string
}

const NAV_ITEMS: readonly NavItem[] = [
  { key: 'home', to: '/', label: 'okf4net.md' },
  { key: 'what-okf-is', to: '/what-okf-is', label: 'what-okf-is.md' },
  { key: 'library', to: '/library', label: 'library.md' },
  { key: 'cli', to: '/cli', label: 'cli.md' },
  { key: 'docs', to: '/docs', label: 'docs.md' },
  { key: 'contributing', to: '/contributing', label: 'contributing.md' },
]

export interface SiteBarProps {
  /**
   * Which nav item represents the page currently being rendered. `undefined`
   * (e.g. the 404 page, which isn't any of these chapters) leaves every item
   * inactive.
   */
  current?: NavKey
}

/**
 * Top site header: wordmark, section nav, github link.
 * Port of `header.bar` (website/index.html:16-29).
 *
 * The active item is driven explicitly by `current` rather than by
 * react-router's automatic URL-prefix matching: several nav entries (e.g.
 * `docs`) will eventually cover a whole subtree of routes, and every page
 * under that subtree already knows its own nav identity, so an explicit
 * comparison is simpler and more robust than re-deriving it from the URL.
 */
export default function SiteBar({ current }: SiteBarProps) {
  return (
    <header className="bar">
      <div className="bar-in">
        <Link className="wordmark" to="/">
          OKF<sup>4net</sup>
        </Link>
        <nav aria-label="Site">
          {NAV_ITEMS.map((item) => (
            <Link key={item.key} to={item.to} aria-current={item.key === current ? 'page' : undefined}>
              {item.label}
            </Link>
          ))}
        </nav>
        <a className="gh" href="https://github.com/jchable/okf4net">
          github ↗
        </a>
      </div>
    </header>
  )
}
