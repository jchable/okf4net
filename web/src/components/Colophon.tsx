// SPDX-License-Identifier: LGPL-3.0-or-later

export type ColophonVariant = 'full' | 'minimal'

export interface ColophonProps {
  /** 'full' (default) renders the `.links` row; 'minimal' drops it (404 page). */
  variant?: ColophonVariant
}

/**
 * Site footer.
 * Port of `footer.colophon` — full variant: website/index.html:166-176;
 * minimal variant (no `.links` row, shorter notice): website/404.html:56-60.
 */
export default function Colophon({ variant = 'full' }: ColophonProps) {
  if (variant === 'minimal') {
    return (
      <footer className="colophon">
        <div className="frame">
          <p>OKF4net — LGPL-3.0-or-later. An independent implementation, not affiliated with or endorsed by Google.</p>
        </div>
      </footer>
    )
  }

  return (
    <footer className="colophon">
      <div className="frame">
        <div className="links">
          <a href="https://github.com/jchable/okf4net">source</a>
          <a href="https://www.nuget.org/packages?q=OKF4net">nuget</a>
          <a href="https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md">okf spec v0.2</a>
          <a href="https://github.com/jchable/okf4net/blob/main/LICENSE">license</a>
        </div>
        <p>
          OKF4net — LGPL-3.0-or-later. Portions derive from the Apache-2.0 OKF reference implementation by Google LLC
          (full attribution in NOTICE). An independent implementation, not affiliated with or endorsed by Google.
        </p>
      </div>
    </footer>
  )
}
