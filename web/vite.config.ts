/// <reference types="vitest/config" />
import { defineConfig, type UserConfig } from 'vite'
import type { ViteReactSSGOptions } from 'vite-react-ssg'
import react from '@vitejs/plugin-react'
// This project has no @types/node (and gains no dependency for it): vite runs this file on Node, so
// the import below resolves at run time and is typed `any` here.
// @ts-expect-error TS2307 — no Node type declarations
import { readFileSync } from 'node:fs'

// The library version shown on the site is read from the repository's single
// source of truth (`<Version>` in Directory.Build.props), so a release that
// bumps it cannot leave the site describing the previous version. The path is
// relative to the working directory: vite, vitest and CI all run from `web/`.
const props = readFileSync('../Directory.Build.props', 'utf8')
const okf4netVersion = /<Version>\s*([^<\s]+)\s*<\/Version>/.exec(props)?.[1]
if (!okf4netVersion) throw new Error('Directory.Build.props declares no <Version>')

// `vite-react-ssg` reads `ssgOptions` off the plain config object at build
// time; it doesn't ship a `defineConfig` of its own or augment Vite's
// `UserConfig`, so the extra key needs this narrow type annotation to keep
// `tsc` happy without changing anything at runtime.
export default defineConfig({
  base: '/okf4net/',
  plugins: [react()],
  define: { __OKF4NET_VERSION__: JSON.stringify(okf4netVersion) },
  // Nested output (`docs/index.html` rather than the default flat
  // `docs.html`) matches the pre-migration static site's directory-per-page
  // layout and gives every route a clean, extension-less URL on static hosts.
  ssgOptions: {
    dirStyle: 'nested',
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.tsx'],
  },
} as UserConfig & { ssgOptions?: Partial<ViteReactSSGOptions> })
