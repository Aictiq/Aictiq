// Refreshes cli/openapi/v1.json from a running Aictiq API and regenerates
// src/api/schema.d.ts from it. Both files are committed: CI has no API to point at, and
// a reviewer should be able to see what changed in the contract, not just in the types.
//
//   pnpm gen:api                                   # http://localhost:5177
//   AICTIQ_URL=https://aictiq.example.com pnpm gen:api
//   pnpm gen:api --offline                         # regenerate types from the saved doc
//
// The contract is public by default. An operator may disable it with
// Documentation:Enabled=false, in which case use --offline or their published artifact.
import { spawnSync } from 'node:child_process'
import { writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const document = join(root, 'openapi', 'v1.json')
const output = join(root, 'src', 'api', 'schema.d.ts')

if (!process.argv.includes('--offline')) {
    const base = (process.env.AICTIQ_URL ?? 'http://localhost:5177').replace(/\/+$/, '')
    const url = `${base}/openapi/v1.json`
    const response = await fetch(url)
    if (!response.ok) {
        console.error(
            `GET ${url} failed with ${response.status}. Is the API running with Documentation:Enabled=true?`,
        )
        process.exit(1)
    }
    // Stable key order and indentation, so a contract change is a readable diff.
    writeFileSync(document, `${JSON.stringify(await response.json(), sortKeys, 2)}\n`)
    console.log(`Wrote ${document} from ${url}`)
}

const generated = spawnSync('pnpm', ['exec', 'openapi-typescript', document, '--output', output], {
    cwd: root,
    stdio: 'inherit',
})
process.exit(generated.status ?? 1)

function sortKeys(_key, value) {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) return value
    return Object.fromEntries(
        Object.entries(value).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)),
    )
}
