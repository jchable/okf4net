// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'
import Layout from './Layout'
import DocsSidebar from '../components/DocsSidebar'

export interface DocsLayoutProps {
  /** Document `<title>`. */
  title: string
  /** `<meta name="description">` content. */
  description: string
  /** `slug` of the docs page currently being rendered — forwarded to `DocsSidebar`. */
  current: string
  children: ReactNode
}

/**
 * Shell for every page under `/docs`: wraps `Layout` (with the top `SiteBar`
 * nav pinned to `current="docs"`, per Task 2 review — the top-nav `docs.md`
 * tab is active on every docs page regardless of which one) and adds the
 * two-column docs bundle layout — `DocsSidebar` plus `.docs-main`.
 *
 * `Layout` already wraps its `children` in exactly one `<div class="frame">`
 * (every OKF4net page has one), so this component must NOT add a second
 * `.frame` — it renders `.docs-shell` directly as that single frame's
 * content, matching the static `<div class="frame"><div class="docs-shell">`
 * structure in `website/docs/index.html`.
 */
export default function DocsLayout({ title, description, current, children }: DocsLayoutProps) {
  return (
    <Layout title={title} description={description} current="docs">
      <div className="docs-shell">
        <DocsSidebar current={current} />
        <div className="docs-main">{children}</div>
      </div>
    </Layout>
  )
}
