import { defineConfig } from 'tsup'

export default defineConfig({
  entry: ['src/index.ts'],
  format: ['esm'],
  target: 'node24',
  platform: 'node',
  clean: true,
  sourcemap: true,
  // The MCP SDK and commander stay external: they are ordinary runtime dependencies
  // resolved from node_modules, and bundling them would hide their licences and make
  // security updates require a CLI release.
  external: ['@modelcontextprotocol/sdk', 'commander'],
  banner: { js: '#!/usr/bin/env node' },
})
