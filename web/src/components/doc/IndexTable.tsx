// SPDX-License-Identifier: LGPL-3.0-or-later
import type { ReactNode } from 'react'

export interface IndexTableRow {
  /** `td.type` content, e.g. `"Guide"`. */
  type: ReactNode
  /**
   * `td.title` content — usually a `<Link>`/`<a>` to the concept; for a
   * not-yet-published concept, pass a `<span className="soon">` label
   * followed by a `<Tag>soon</Tag>`, matching `website/docs/index.html`'s
   * "agents" row.
   */
  concept: ReactNode
  /** `td.desc` content. */
  desc: ReactNode
}

export interface IndexTableProps {
  rows: IndexTableRow[]
}

/**
 * The generated `index.md` listing table (§6).
 * Port of `table.index` — see the specimen source in
 * `design-system/styleguide.html:229-237` and full usage in
 * `website/docs/index.html:73-93` (the "soon" row uses `.soon` + `Tag`).
 */
export default function IndexTable({ rows }: IndexTableProps) {
  return (
    <table className="index">
      <thead>
        <tr>
          <th>Type</th>
          <th>Concept</th>
          <th>Description</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row, i) => (
          <tr key={i}>
            <td className="type">{row.type}</td>
            <td className="title">{row.concept}</td>
            <td className="desc">{row.desc}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
