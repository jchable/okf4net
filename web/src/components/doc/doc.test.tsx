// SPDX-License-Identifier: LGPL-3.0-or-later
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import {
  Cell,
  Chapter,
  ConceptGrid,
  Conform,
  Cta,
  IndexTable,
  MapTable,
  Next,
  PageDoc,
  Steps,
  Tag,
  Term,
  Warn,
} from './index'

/**
 * Minimal render check (Task 3, Step 2): mounts each `doc/` device and
 * asserts the DOM shape callers rely on — root class, and the handful of
 * nested elements (`.chead`, `.t`, `<thead>`/`<tbody>`, …) that make these
 * components more than a one-line wrapper. Not a snapshot/visual test —
 * just confirmation each component actually emits the site.css contract
 * documented in design-system/styleguide.html.
 */

let container: HTMLDivElement | undefined
let root: Root | undefined

function mount(node: React.ReactElement): HTMLDivElement {
  container = document.createElement('div')
  document.body.appendChild(container)
  root = createRoot(container)
  act(() => {
    root!.render(node)
  })
  return container
}

afterEach(() => {
  if (root) {
    act(() => {
      root!.unmount()
    })
  }
  container?.remove()
  container = undefined
  root = undefined
})

describe('doc device components', () => {
  it('PageDoc renders .page-doc > .doc-window > .doc-chrome + .rendered(h1 + .lede)', () => {
    const el = mount(
      <PageDoc path="my_bundle/what-okf-is.md" type="Guide" title="A knowledge format you can cat." lede="The lede." />,
    )
    const pageDoc = el.querySelector(':scope > .page-doc')
    expect(pageDoc).not.toBeNull()
    expect(pageDoc!.querySelector('.doc-window > .doc-chrome > .path')?.textContent).toBe('my_bundle/what-okf-is.md')
    expect(pageDoc!.querySelector('.doc-window > .doc-chrome > span:not(.path)')?.textContent).toBe('type: Guide')
    expect(pageDoc!.querySelector('.rendered > h1')?.textContent).toBe('A knowledge format you can cat.')
    expect(pageDoc!.querySelector('.rendered > p.lede')?.textContent).toBe('The lede.')
  })

  it('Chapter renders section.chapter#id > .chead(.h + h2 + .ref) + children', () => {
    const el = mount(
      <Chapter id="terms" title="Terminology" refText="§2–§3">
        <p>body</p>
      </Chapter>,
    )
    const section = el.querySelector('section.chapter#terms')
    expect(section).not.toBeNull()
    expect(section!.querySelector('.chead > .h')?.textContent).toBe('##')
    expect(section!.querySelector('.chead > h2')?.textContent).toBe('Terminology')
    expect(section!.querySelector('.chead > .ref')?.textContent).toBe('§2–§3')
    expect(section!.querySelector('p')?.textContent).toBe('body')
  })

  it('ConceptGrid/Cell/Term render .concept-grid > .cell > (.term + p)', () => {
    const el = mount(
      <ConceptGrid>
        <Cell>
          <Term>bundle</Term>
          <p>def</p>
        </Cell>
      </ConceptGrid>,
    )
    const grid = el.querySelector('.concept-grid')
    expect(grid).not.toBeNull()
    expect(grid!.querySelector('.cell > .term')?.textContent).toBe('bundle')
    expect(grid!.querySelector('.cell > p')?.textContent).toBe('def')
  })

  it('MapTable renders table.map with thead/tbody and the given rows', () => {
    const el = mount(
      <MapTable
        head={['Command', 'Does']}
        rows={[
          ['okf validate', 'Conformance check'],
          ['okf graph', 'Cross-link graph'],
        ]}
      />,
    )
    const table = el.querySelector('table.map')
    expect(table).not.toBeNull()
    expect(table!.querySelectorAll('thead th')).toHaveLength(2)
    expect(table!.querySelector('thead th')?.textContent).toBe('Command')
    expect(table!.querySelectorAll('tbody tr')).toHaveLength(2)
    expect(table!.querySelectorAll('tbody tr')[0].querySelectorAll('td')[1].textContent).toBe('Conformance check')
  })

  it('IndexTable renders table.index with td.type/td.title/td.desc per row', () => {
    const el = mount(
      <IndexTable
        rows={[
          { type: 'Guide', concept: <a href="getting-started.html">getting-started</a>, desc: 'Install.' },
          {
            type: 'Reference',
            concept: (
              <>
                <span className="soon">agents</span>
                <Tag>soon</Tag>
              </>
            ),
            desc: 'The Agent Framework tools.',
          },
        ]}
      />,
    )
    const table = el.querySelector('table.index')
    expect(table).not.toBeNull()
    const rows = table!.querySelectorAll('tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0].querySelector('td.type')?.textContent).toBe('Guide')
    expect(rows[0].querySelector('td.title a')?.getAttribute('href')).toBe('getting-started.html')
    expect(rows[1].querySelector('td.title .soon')?.textContent).toBe('agents')
    expect(rows[1].querySelector('td.title .tag')?.textContent).toBe('soon')
  })

  it('Steps renders ol.steps with li children', () => {
    const el = mount(
      <Steps>
        <li>one</li>
        <li>two</li>
      </Steps>,
    )
    const ol = el.querySelector('ol.steps')
    expect(ol).not.toBeNull()
    expect(ol!.querySelectorAll(':scope > li')).toHaveLength(2)
  })

  it('Warn renders .warn > .t(title) + children', () => {
    const el = mount(
      <Warn title="GOLDEN FIXTURES">
        <p>Never edit tests/fixtures/.</p>
      </Warn>,
    )
    const warn = el.querySelector('.warn')
    expect(warn).not.toBeNull()
    expect(warn!.querySelector('.t')?.textContent).toBe('GOLDEN FIXTURES')
    expect(warn!.querySelector('p')?.textContent).toBe('Never edit tests/fixtures/.')
  })

  it('Next renders p.next', () => {
    const el = mount(<Next>→ next.md</Next>)
    expect(el.querySelector('p.next')?.textContent).toBe('→ next.md')
  })

  it('Cta renders .cta > h2(title) + children', () => {
    const el = mount(
      <Cta title="Ship knowledge as files.">
        <p>Star the repo.</p>
      </Cta>,
    )
    const cta = el.querySelector('.cta')
    expect(cta).not.toBeNull()
    expect(cta!.querySelector('h2')?.textContent).toBe('Ship knowledge as files.')
    expect(cta!.querySelector('p')?.textContent).toBe('Star the repo.')
  })

  it('Conform renders .conform', () => {
    const el = mount(<Conform>conformant with OKF v0.1</Conform>)
    expect(el.querySelector('.conform')?.textContent).toBe('conformant with OKF v0.1')
  })

  it('Tag renders span.tag', () => {
    const el = mount(<Tag>soon</Tag>)
    expect(el.querySelector('span.tag')?.textContent).toBe('soon')
  })
})
