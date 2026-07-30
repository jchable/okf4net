import type { RouteRecord } from 'vite-react-ssg'
import Home from './pages/Home'
import DocsIndex from './pages/docs/Index'
import GettingStarted from './pages/docs/GettingStarted'
import Guides from './pages/docs/Guides'
import DocsLibrary from './pages/docs/Library'
import DocsCli from './pages/docs/Cli'
import Agents from './pages/docs/Agents'
import Catalog from './pages/docs/Catalog'
import Mcp from './pages/docs/Mcp'
import Spec from './pages/docs/Spec'
import WhatOkfIs from './pages/WhatOkfIs'
import Library from './pages/Library'
import Cli from './pages/Cli'
import Contributing from './pages/Contributing'
import Support from './pages/Support'
import NotFound from './pages/NotFound'

export const routes: RouteRecord[] = [
  { path: '/', element: <Home />, entry: 'src/pages/Home.tsx' },
  { path: '/docs', element: <DocsIndex />, entry: 'src/pages/docs/Index.tsx' },
  { path: '/docs/getting-started', element: <GettingStarted />, entry: 'src/pages/docs/GettingStarted.tsx' },
  { path: '/docs/guides', element: <Guides />, entry: 'src/pages/docs/Guides.tsx' },
  { path: '/docs/library', element: <DocsLibrary />, entry: 'src/pages/docs/Library.tsx' },
  { path: '/docs/cli', element: <DocsCli />, entry: 'src/pages/docs/Cli.tsx' },
  { path: '/docs/agents', element: <Agents />, entry: 'src/pages/docs/Agents.tsx' },
  { path: '/docs/catalog', element: <Catalog />, entry: 'src/pages/docs/Catalog.tsx' },
  { path: '/docs/mcp', element: <Mcp />, entry: 'src/pages/docs/Mcp.tsx' },
  { path: '/docs/spec', element: <Spec />, entry: 'src/pages/docs/Spec.tsx' },
  { path: '/what-okf-is', element: <WhatOkfIs />, entry: 'src/pages/WhatOkfIs.tsx' },
  { path: '/library', element: <Library />, entry: 'src/pages/Library.tsx' },
  { path: '/cli', element: <Cli />, entry: 'src/pages/Cli.tsx' },
  { path: '/contributing', element: <Contributing />, entry: 'src/pages/Contributing.tsx' },
  { path: '/support', element: <Support />, entry: 'src/pages/Support.tsx' },
  // Explicit /404 route so the SSG build emits dist/404/index.html (Task 7
  // copies it to dist/404.html for GitHub Pages); '*' is the client-side
  // catch-all for unknown-route navigation within the SPA.
  { path: '/404', element: <NotFound />, entry: 'src/pages/NotFound.tsx' },
  { path: '*', element: <NotFound />, entry: 'src/pages/NotFound.tsx' },
]
