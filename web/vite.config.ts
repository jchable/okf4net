/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  base: '/okf4net/',
  plugins: [react()],
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.tsx'],
  },
})
