import { ViteReactSSG } from 'vite-react-ssg'
import { routes } from './routes'
import './styles/site.css'

export const createRoot = ViteReactSSG(
  { routes, basename: '/okf4net' },
)
