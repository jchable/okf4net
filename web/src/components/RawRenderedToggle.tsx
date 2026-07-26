// SPDX-License-Identifier: LGPL-3.0-or-later
import { useState } from 'react'
import { Link } from 'react-router-dom'

type ViewMode = 'raw' | 'rendered'

/**
 * Raw ⇄ rendered toggle for the hero concept-document window.
 * Port of `website/assets/site.js` (DOM `classList`/`aria-pressed` toggling)
 * plus the `.doc-window` markup it operates on (website/index.html:36-77),
 * reimplemented with React state instead of imperative DOM manipulation.
 */
export default function RawRenderedToggle() {
  const [mode, setMode] = useState<ViewMode>('rendered')
  const isRaw = mode === 'raw'

  return (
    <div className="doc-window">
      <div className="doc-chrome">
        <span className="path">
          my_bundle/<b>okf4net.md</b>
        </span>
        <div className="toggle" role="group" aria-label="View mode">
          <button id="btn-raw" aria-pressed={isRaw} onClick={() => setMode('raw')}>
            raw
          </button>
          <button id="btn-rendered" aria-pressed={!isRaw} onClick={() => setMode('rendered')}>
            rendered
          </button>
        </div>
      </div>

      <div className={`pane raw${isRaw ? ' visible' : ''}`} id="pane-raw" aria-label="Raw OKF source">
        <span className="ln fm" data-n="1"><span className="delim">---</span></span>
        <span className="ln fm" data-n="2"><span className="key">type</span>: Library</span>
        <span className="ln fm" data-n="3"><span className="key">title</span>: OKF4net</span>
        <span className="ln fm" data-n="4"><span className="key">description</span>: <span className="str">Zero-dependency .NET implementation of OKF v0.1</span></span>
        <span className="ln fm" data-n="5"><span className="key">tags</span>: [<span className="str">dotnet</span>, <span className="str">knowledge</span>, <span className="str">markdown</span>]</span>
        <span className="ln fm" data-n="6"><span className="delim">---</span></span>
        <span className="ln" data-n="7"> </span>
        <span className="ln" data-n="8"><span className="md-h"># OKF4net</span></span>
        <span className="ln" data-n="9"> </span>
        <span className="ln" data-n="10">Knowledge is a directory of markdown files.</span>
        <span className="ln" data-n="11">This is its .NET toolchain — parse, validate, index</span>
        <span className="ln" data-n="12">and graph <span className="md-link">[bundles](./what-okf-is.md)</span> of concepts, on nothing</span>
        <span className="ln" data-n="13">but the .NET base class library.</span>
        <span className="ln" data-n="14"> </span>
        <span className="ln" data-n="15">If you can `cat` a file, you can read OKF.</span>
      </div>

      <div className={`pane rendered${!isRaw ? ' visible' : ''}`} id="pane-rendered">
        <table className="fm-table" aria-label="Frontmatter">
          <tbody>
            <tr><td>type</td><td>Library</td></tr>
            <tr><td>title</td><td>OKF4net</td></tr>
            <tr><td>description</td><td>Zero-dependency .NET implementation of OKF v0.1</td></tr>
          </tbody>
        </table>
        <h1>Knowledge is a directory of <em>markdown files.</em></h1>
        <p className="lede">OKF4net is its .NET toolchain — a <strong>zero-dependency C# library</strong> and a Native AOT <strong><code>okf</code> CLI</strong> to parse, validate, index and graph bundles of concepts.</p>
        <div className="hero-actions">
          <a className="btn primary" href="https://www.nuget.org/packages/OKF4net">dotnet add package OKF4net</a>
          <Link className="btn" to="/library">Read library.md</Link>
        </div>
        <div className="conform">conformant with OKF v0.1 · <b>218/218</b> tests · 5 byte-exact golden comparisons</div>
      </div>
    </div>
  )
}
