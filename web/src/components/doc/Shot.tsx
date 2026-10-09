// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface ShotProps {
  /** File name under `public/viewer/`, e.g. `"viewer-concept.png"`. */
  file: string
  /** Meaningful alternative text: what the screenshot shows, not "screenshot". */
  alt: string
  /** Intrinsic width in pixels (the PNG's own), so the browser reserves the box. */
  width: number
  /** Intrinsic height in pixels (the PNG's own). */
  height: number
  /** Widest the figure is drawn, in CSS pixels — half of `width` for a 2x capture. */
  maxWidth: number
  /** Caption under the image. */
  children: ReactNode
}

/**
 * A screenshot: a lazy-loaded, size-attributed `<img>` that links to the
 * full-size file, with a caption. The URL is built from Vite's `BASE_URL`
 * (`/okf4net/` on GitHub Pages), because `public/` files are not rewritten
 * by the router and a root-relative `/viewer/x.png` would 404 there.
 */
export default function Shot({ file, alt, width, height, maxWidth, children }: ShotProps) {
  const src = `${import.meta.env.BASE_URL}viewer/${file}`
  return (
    <figure className="shot" style={{ maxWidth }}>
      <a href={src}>
        <img src={src} alt={alt} width={width} height={height} loading="lazy" decoding="async" />
      </a>
      <figcaption>{children}</figcaption>
    </figure>
  )
}
