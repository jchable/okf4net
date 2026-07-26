// SPDX-License-Identifier: LGPL-3.0-or-later
// Copies dist/404/index.html -> dist/404.html so GitHub Pages (which serves
// /404.html for unknown paths) renders the SSG-built 404 page. Runs
// automatically after `npm run build` via the package.json `postbuild` hook.
import { copyFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const here = path.dirname(fileURLToPath(import.meta.url))
const distDir = path.join(here, '..', 'dist')
const src = path.join(distDir, '404', 'index.html')
const dest = path.join(distDir, '404.html')

copyFileSync(src, dest)
console.log(`postbuild: copied ${path.relative(distDir, src)} -> ${path.relative(distDir, dest)}`)
