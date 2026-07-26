/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

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
})
