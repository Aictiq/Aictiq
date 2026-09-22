import type { HarnessAdapter, HarnessInfo, HarnessName } from '../types.js'
import { claude } from './claude.js'
import { codex } from './codex.js'
import { opencode } from './opencode.js'

export { versionOf } from './claude.js'

export const harnesses: Record<HarnessName, HarnessAdapter> = { claude, codex, opencode }

export async function probeHarnesses(env?: NodeJS.ProcessEnv): Promise<HarnessInfo[]> {
  const found = await Promise.all(Object.values(harnesses).map((harness) => harness.available(env)))
  return found.filter((info): info is HarnessInfo => info !== null)
}
