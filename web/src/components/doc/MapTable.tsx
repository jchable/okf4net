// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface MapTableProps {
  /** The two column headers, e.g. `["Spec section", "Implemented by"]`. */
  head: [string, string]
  /** Body rows, each a `[left, right]` pair of cell content. */
  rows: [ReactNode, ReactNode][]
}

/**
 * The mono-key + prose reference table.
 * Port of `table.map` — see the specimen source in
 * `design-system/styleguide.html:218-226` and usage in
 * `website/what-okf-is.html:90-101`.
 *
 * The source markup has no `<thead>`/`<tbody>` (plain `<tr>` children of
 * `<table>`), but `table.map`'s CSS selectors (`table.map th`, `table.map
 * td`) aren't scoped to direct children, so adding `<thead>`/`<tbody>` here
 * — required to avoid React's "table rows must be children of tbody" DOM
 * warning — changes nothing visually.
 */
export default function MapTable({ head, rows }: MapTableProps) {
  return (
    <table className="map">
      <thead>
        <tr>
          <th>{head[0]}</th>
          <th>{head[1]}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row, i) => (
          <tr key={i}>
            <td>{row[0]}</td>
            <td>{row[1]}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
