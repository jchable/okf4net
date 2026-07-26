import type { RouteRecord } from 'vite-react-ssg'
import Home from './pages/Home'
import DocsIndex from './pages/docs/Index'
import WhatOkfIs from './pages/WhatOkfIs'
import Library from './pages/Library'
import Cli from './pages/Cli'
import Contributing from './pages/Contributing'
import NotFound from './pages/NotFound'

export const routes: RouteRecord[] = [
  { path: '/', element: <Home />, entry: 'src/pages/Home.tsx' },
  { path: '/docs', element: <DocsIndex />, entry: 'src/pages/docs/Index.tsx' },
  { path: '/what-okf-is', element: <WhatOkfIs />, entry: 'src/pages/WhatOkfIs.tsx' },
  { path: '/library', element: <Library />, entry: 'src/pages/Library.tsx' },
  { path: '/cli', element: <Cli />, entry: 'src/pages/Cli.tsx' },
  { path: '/contributing', element: <Contributing />, entry: 'src/pages/Contributing.tsx' },
  // Explicit /404 route so the SSG build emits dist/404/index.html (Task 7
  // copies it to dist/404.html for GitHub Pages); '*' is the client-side
  // catch-all for unknown-route navigation within the SPA.
  { path: '/404', element: <NotFound />, entry: 'src/pages/NotFound.tsx' },
  { path: '*', element: <NotFound />, entry: 'src/pages/NotFound.tsx' },
]
