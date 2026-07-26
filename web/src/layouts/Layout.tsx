// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'
import { Head } from 'vite-react-ssg'
import SiteBar, { type NavKey } from '../components/SiteBar'
import Colophon, { type ColophonVariant } from '../components/Colophon'

export interface LayoutProps {
  /** Document `<title>`. */
  title: string
  /** `<meta name="description">` content. */
  description: string
  /** Which SiteBar nav item is active for this page; omit to leave every item inactive (404 page). */
  current?: NavKey
  /** Colophon variant; defaults to the full footer with the `.links` row. */
  footerVariant?: ColophonVariant
  /** When true, renders `<meta name="robots" content="noindex">` (404 page). */
  noindex?: boolean
  children: ReactNode
}

/**
 * Shared page chrome: head metadata, `.topline`, `SiteBar`, the `.frame`
 * content wrapper every page uses, and `Colophon`.
 *
 * Every OKF4net page (website/index.html, website/404.html, and every
 * chapter page in between) wraps its unique body in exactly one
 * `<div class="frame">` between the header and the footer, so that wrapper
 * lives here rather than being repeated per page.
 */
export default function Layout({ title, description, current, footerVariant, noindex, children }: LayoutProps) {
  return (
    <>
      <Head>
        <title>{title}</title>
        <meta name="description" content={description} />
        {noindex && <meta name="robots" content="noindex" />}
      </Head>
      <div className="topline" />
      <SiteBar current={current} />
      <div className="frame">{children}</div>
      <Colophon variant={footerVariant} />
    </>
  )
}
