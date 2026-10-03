import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

// Read at runtime rather than baked in by the bundler, so `aictiq --version` and the MCP
// handshake cannot drift from the version npm actually installed.
const manifest = createRequire(import.meta.url)('../package.json') as { version?: string }

export const version = manifest.version ?? '0.0.0'

/** The installed package's directory: `dist/` is bundled flat, so its parent. */
export const packageRoot = fileURLToPath(new URL('..', import.meta.url))
