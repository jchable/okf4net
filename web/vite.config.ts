/// <reference types="vitest/config" />
import { defineConfig, type UserConfig } from 'vite'
import type { ViteReactSSGOptions } from 'vite-react-ssg'
import react from '@vitejs/plugin-react'

// `vite-react-ssg` reads `ssgOptions` off the plain config object at build
// time; it doesn't ship a `defineConfig` of its own or augment Vite's
// `UserConfig`, so the extra key needs this narrow type annotation to keep
// `tsc` happy without changing anything at runtime.
export default defineConfig({
  base: '/okf4net/',
  plugins: [react()],
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
