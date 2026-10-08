import { findOnPath, pathKey } from '../path.js'
import type { HarnessAdapter, HarnessInfo, HarnessName, MissingHarness } from '../types.js'
import { claude } from './claude.js'
import { codex } from './codex.js'
import { copilot } from './copilot.js'
import { cursor } from './cursor.js'
import { opencode } from './opencode.js'

export { versionOf } from './claude.js'

export const harnesses: Record<HarnessName, HarnessAdapter> = {
  claude,
  codex,
  opencode,
  cursor,
  copilot,
}

/** The executables each adapter's `available` asks for `--version`, in its order. */
export const HarnessCommands: Record<HarnessName, readonly string[]> = {
  claude: ['claude'],
  codex: ['codex'],
  opencode: ['opencode'],
  cursor: ['agent', 'cursor-agent'],
  copilot: ['copilot'],
}

export async function probeHarnesses(env?: NodeJS.ProcessEnv): Promise<HarnessInfo[]> {
  return (await probeAllHarnesses(env)).harnesses
}

/**
 * Every harness the CLI knows: the ones found, and why each other one was not - absent from
 * PATH, or present but its `--version` failed - so a runner can say which to fix.
 */
export async function probeAllHarnesses(
  env: NodeJS.ProcessEnv = process.env,
): Promise<{ harnesses: HarnessInfo[]; missing: MissingHarness[] }> {
  const adapters = Object.values(harnesses)
  const found = await Promise.all(adapters.map((harness) => harness.available(env)))
  const path = env[pathKey(env)] ?? ''
  const missing = adapters.flatMap((harness, i): MissingHarness[] => {
    if (found[i]) return []
    const command = HarnessCommands[harness.name].find((c) =>
      findOnPath(c, path, process.platform, env),
    )
    return [
      command
        ? { name: harness.name, reason: 'version-failed', command }
        : { name: harness.name, reason: 'not-on-path', command: HarnessCommands[harness.name][0]! },
    ]
  })
  return { harnesses: found.filter((info): info is HarnessInfo => info !== null), missing }
}

/** One line for a person: why `missing` was not offered. */
export function describeMissing(missing: MissingHarness): string {
  return missing.reason === 'not-on-path'
    ? `\`${missing.command}\` is not on PATH`
    : `\`${missing.command} --version\` failed${missing.name === 'cursor' ? ' or is not Cursor’s' : ''}`
}
