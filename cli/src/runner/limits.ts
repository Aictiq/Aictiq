import { mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { runnerConfigPath } from './config.js'
import type { HarnessName, HarnessUsageLimits, HarnessUsageWindow, ParsedLine } from './types.js'

type Windows = NonNullable<ParsedLine['limits']>

const KnownHarnesses: readonly HarnessName[] = ['claude', 'codex', 'opencode', 'cursor', 'copilot']

/**
 * The last usage each harness reported, kept beside runner.json. It is the machine's: the
 * harness accounts are signed in once per machine, so every organization's runner reports the
 * same file, and a restart does not forget what the last run saw.
 */
export function usageLimitsPath(env: NodeJS.ProcessEnv = process.env): string {
  return join(dirname(runnerConfigPath(env)), 'harness-limits.json')
}

/** Empty when the file is missing or unreadable: usage is display only. */
export function readUsageLimits(path = usageLimitsPath()): HarnessUsageLimits[] {
  try {
    const parsed = JSON.parse(readFileSync(path, 'utf8')) as unknown
    return Array.isArray(parsed) ? parsed.filter(isUsageLimits) : []
  } catch {
    return []
  }
}

/**
 * Records what a run saw for its harness. A window the run did not report keeps its stored
 * value, and an older observation never replaces a newer one another run wrote meanwhile.
 */
export function recordUsageLimits(
  harness: HarnessName,
  windows: Windows,
  observedAt: Date,
  path = usageLimitsPath(),
): HarnessUsageLimits | null {
  if (windows.fiveHour === undefined && windows.weekly === undefined) return null
  const stored = readUsageLimits(path)
  const previous = stored.find((entry) => entry.harness === harness)
  if (previous && Date.parse(previous.observedAt) > observedAt.getTime()) return previous
  const next: HarnessUsageLimits = {
    harness,
    observedAt: observedAt.toISOString(),
    fiveHour: windows.fiveHour === undefined ? (previous?.fiveHour ?? null) : windows.fiveHour,
    weekly: windows.weekly === undefined ? (previous?.weekly ?? null) : windows.weekly,
  }
  try {
    mkdirSync(dirname(path), { recursive: true })
    const temporary = `${path}.${process.pid}.tmp`
    writeFileSync(
      temporary,
      `${JSON.stringify([...stored.filter((entry) => entry !== previous), next], null, 2)}\n`,
    )
    renameSync(temporary, path)
  } catch {
    // Display only: a read-only config directory must not fail the run.
  }
  return next
}

/** A window from a share of its allowance, and a reset as Unix seconds or ISO text. */
export function usageWindow(usedPercent: unknown, resetsAt: unknown): HarnessUsageWindow | null {
  if (typeof usedPercent !== 'number' || !Number.isFinite(usedPercent)) return null
  const reset =
    typeof resetsAt === 'number' && Number.isFinite(resetsAt)
      ? new Date(resetsAt * 1000)
      : typeof resetsAt === 'string'
        ? new Date(resetsAt)
        : null
  return {
    usedPercent: Math.min(1000, Math.max(0, Math.round(usedPercent * 10) / 10)),
    resetsAt: reset && !Number.isNaN(reset.getTime()) ? reset.toISOString() : null,
  }
}

function isUsageLimits(value: unknown): value is HarnessUsageLimits {
  const entry = value as Partial<HarnessUsageLimits> | null
  return (
    typeof entry === 'object' &&
    entry !== null &&
    KnownHarnesses.includes(entry.harness as HarnessName) &&
    typeof entry.observedAt === 'string' &&
    !Number.isNaN(Date.parse(entry.observedAt)) &&
    isWindow(entry.fiveHour) &&
    isWindow(entry.weekly)
  )
}

function isWindow(value: unknown): boolean {
  if (value === null) return true
  const window = value as Partial<HarnessUsageWindow> | undefined
  return (
    typeof window === 'object' &&
    typeof window.usedPercent === 'number' &&
    (window.resetsAt === null || typeof window.resetsAt === 'string')
  )
}
