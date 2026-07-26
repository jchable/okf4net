import type { RouteRecord } from 'vite-react-ssg'
import Home from './pages/Home'
import DocsIndex from './pages/docs/Index'

export const routes: RouteRecord[] = [
  { path: '/', element: <Home />, entry: 'src/pages/Home.tsx' },
  { path: '/docs', element: <DocsIndex />, entry: 'src/pages/docs/Index.tsx' },
]
