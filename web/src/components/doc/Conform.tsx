// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface ConformProps {
  /** Usually `conformant with OKF v0.1 · <b>218/218</b> tests · …`. */
  children: ReactNode
}

/**
 * The dashed conformance badge.
 * Port of `.conform` — see the specimen source in
 * `design-system/styleguide.html:265-273` and usage in
 * `website/index.html:75`.
 */
export default function Conform({ children }: ConformProps) {
  return <div className="conform">{children}</div>
}
