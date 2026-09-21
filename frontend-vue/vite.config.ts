import { fileURLToPath, URL } from 'node:url'
import { gzipSync } from 'node:zlib'

import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import type { Plugin } from 'vite'
// vitest/config re-exports Vite's defineConfig with the `test` block typed, so the dev
// server, the build and the test runner all read one config.
import { defineConfig } from 'vitest/config'

/**
 * The SPA is same-origin with the API: the browser only ever talks to this origin, and
 * the API's auth cookies are set on it. In dev that is faked by proxying every backend
 * prefix to the API; in production the API serves `dist/` itself (see Aictiq.Api.csproj).
 *
 * AICTIQ_API_BASE is injected by Aspire (`AddViteApp(...).WithEnvironment(...)`).
 */
const apiBase = process.env.AICTIQ_API_BASE ?? 'http://localhost:5177'

const backendPrefixes = ['/api', '/mcp', '/hubs', '/s3']
const initialBundleBudgetBytes = 350 * 1024

/**
 * A route may be as large as it needs to be once requested, but the modules needed to
 * render the first route must remain cheap to download. Vite's size warning is based on
 * uncompressed individual chunks, which neither models the initial dependency graph nor
 * fails CI; this plugin does both.
 */
function initialBundleBudget(): Plugin {
  return {
    name: 'aictiq-initial-bundle-budget',
    generateBundle(_, bundle) {
      const chunks = Object.values(bundle).filter((asset) => asset.type === 'chunk')
      const byFileName = new Map(chunks.map((chunk) => [chunk.fileName, chunk]))
      const initialFiles = new Set<string>()

      function includeChunk(fileName: string) {
        if (initialFiles.has(fileName)) return
        initialFiles.add(fileName)
        const chunk = byFileName.get(fileName)
        if (!chunk) return
        for (const imported of chunk.imports) includeChunk(imported)
        // CSS listed here is loaded alongside the chunk, so it belongs to its initial cost.
        for (const css of chunk.viteMetadata?.importedCss ?? []) initialFiles.add(css)
      }

      for (const chunk of chunks) if (chunk.isEntry) includeChunk(chunk.fileName)

      const bytes = [...initialFiles].reduce((total, fileName) => {
        const asset = bundle[fileName]
        if (!asset) return total
        const source = asset.type === 'chunk' ? asset.code : asset.source
        return total + gzipSync(source).byteLength
      }, 0)

      if (bytes > initialBundleBudgetBytes) {
        throw new Error(
          `Initial bundle is ${(bytes / 1024).toFixed(1)} kB gzip; budget is ${initialBundleBudgetBytes / 1024} kB.`,
        )
      }
    },
  }
}

export default defineConfig(({ command }) => ({
  plugins: [vue(), tailwindcss(), initialBundleBudget()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: Object.fromEntries(
      backendPrefixes.map((prefix) => [
        prefix,
        {
          target: apiBase,
          changeOrigin: false,
          // /hubs is SignalR; the others never upgrade, and allowing it is harmless.
          ws: true,
        },
      ]),
    ),
  },
  build: {
    outDir: 'dist',
    // Source maps ship to whoever opens devtools, so they stay in dev only.
    sourcemap: command === 'serve',
  },
  test: {
    environment: 'happy-dom',
    include: ['tests/**/*.spec.ts'],
  },
}))
