// SPDX-License-Identifier: LGPL-3.0-or-later

/**
 * Barrel for the ~10 reusable in-body "device" components — thin wrappers
 * that emit the existing `site.css` classes documented in
 * `design-system/styleguide.html`, so page ports use `<Chapter>`,
 * `<ConceptGrid>`, etc. instead of hand-written markup.
 */
export { default as PageDoc, type PageDocProps } from './PageDoc'
export { default as Chapter, type ChapterProps } from './Chapter'
export { default as ConceptGrid, Cell, Term, type ConceptGridProps, type CellProps, type TermProps } from './ConceptGrid'
export { default as MapTable, type MapTableProps } from './MapTable'
export { default as IndexTable, type IndexTableProps, type IndexTableRow } from './IndexTable'
export { default as Steps, type StepsProps } from './Steps'
export { default as Warn, type WarnProps } from './Warn'
export { default as Next, type NextProps } from './Next'
export { default as Cta, type CtaProps } from './Cta'
export { default as Conform, type ConformProps } from './Conform'
export { default as Tag, type TagProps } from './Tag'
