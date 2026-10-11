/**
 * Fleet mode's presentation rules: what a tag may look like, which tags no runner has ever
 * had, and how long the oldest waiting run has waited. Kept out of the components because
 * the same rules apply on four screens and are worth asserting directly.
 */

/** Mirrors the API's rule; the server lower-cases and trims too, so this only spares a round trip. */
export const fleetTagPattern = /^[a-z0-9][a-z0-9._:-]{0,49}$/

/** The API refuses more than this many tags on one runner, playbook, rule or run. */
export const maxFleetTags = 20

/** A tag as the API stores it: trimmed and lower case. */
export function normalizeFleetTag(value: string): string {
  return value.trim().toLowerCase()
}

export function isValidFleetTag(tag: string): boolean {
  return fleetTagPattern.test(tag)
}

/**
 * The required tags no runner has ever had. A run that requires one of them waits until a
 * runner with it connects, which may be exactly what the person wants (an ephemeral runner
 * started by scale from zero), so this only ever warns.
 */
export function unknownFleetTags(tags: readonly string[], knownTags: readonly string[]): string[] {
  const known = new Set(knownTags)
  return tags.filter((tag) => !known.has(tag))
}

/** The non-blocking warning shown under a required-tags field for one unknown tag. */
export function unknownFleetTagWarning(tag: string): string {
  return `No runner has ever had the tag \`${tag}\`. Runs that require it will wait until one does.`
}

/** True when two tag lists hold the same tags, in any order. */
export function sameFleetTags(left: readonly string[], right: readonly string[]): boolean {
  if (left.length !== right.length) return false
  const set = new Set(left)
  return right.every((tag) => set.has(tag))
}

/** "under a minute", "4 min", "2 h 5 min", "3 d": how long the oldest run has waited. */
export function waitedFor(since: string | null | undefined, now: Date = new Date()): string {
  if (!since) return ''
  const then = new Date(since).getTime()
  if (Number.isNaN(then)) return ''
  const minutes = Math.floor(Math.max(0, now.getTime() - then) / 60_000)
  if (minutes < 1) return 'under a minute'
  if (minutes < 60) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return minutes % 60 ? `${hours} h ${minutes % 60} min` : `${hours} h`
  return `${Math.floor(hours / 24)} d`
}

/** "30 s", "5 min", "1 h 30 min": a scale threshold or cooldown said back. */
export function durationLabel(seconds: number): string {
  if (seconds < 60) return `${seconds} s`
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  return minutes % 60 ? `${hours} h ${minutes % 60} min` : `${hours} h`
}

/**
 * What a machine started by scale from zero runs: enroll with the fleet's token (one runner
 * per machine, tagged so required tags find it), then work until idle and remove itself.
 */
export function fleetEnrollCommand(origin: string, token: string, tag = 'gpu-enabled'): string {
  return `aictiq runner enroll --url ${origin} --token ${token} --tag ${tag} && aictiq runner start --idle-exit 10`
}

/** Where a runner's tag comes from: an Admin here (editable), the CLI's `--tag`, or its platform. */
export type RunnerTagSource = 'admin' | 'cli' | 'platform'

export interface RunnerTagChip {
  tag: string
  source: RunnerTagSource
}

/**
 * A runner's effective tags, each with where it came from. Only the admin ones can be edited
 * here; a tag both an Admin and the CLI gave counts as the Admin's, since removing it here
 * is meaningful only for that copy. Admin tags come first, then the CLI's, then the platform.
 */
export function runnerTagChips(runner: {
  tags?: string[] | null
  effectiveTags?: string[] | null
  capabilities?: { tags?: string[] | null } | null
}): RunnerTagChip[] {
  const admin = runner.tags ?? []
  const cli = runner.capabilities?.tags ?? []
  const effective = runner.effectiveTags ?? [...admin, ...cli]
  const order: Record<RunnerTagSource, number> = { admin: 0, cli: 1, platform: 2 }
  return [...new Set(effective)]
    .map((tag): RunnerTagChip => ({
      tag,
      source: admin.includes(tag) ? 'admin' : cli.includes(tag) ? 'cli' : 'platform',
    }))
    .sort((left, right) => order[left.source] - order[right.source])
}

export const runnerTagSourceLabel: Record<RunnerTagSource, string> = {
  admin: 'Set here by an Admin',
  cli: 'Set on the machine with --tag',
  platform: "The runner's platform",
}

/** Runs waiting for any runner, plus those waiting for one in particular: the whole queue. */
export function fleetWaitingCount(
  load: { unassignedQueued: number; runners: { queued: number }[] } | null,
): number {
  if (!load) return 0
  return load.unassignedQueued + load.runners.reduce((total, entry) => total + entry.queued, 0)
}

/** The API's messages about required tags, whether keyed `requiredTags` or `requiredTags[2]`. */
export function requiredTagErrors(fieldErrors: Record<string, string[]>): string[] {
  return Object.entries(fieldErrors)
    .filter(([key]) => key === 'requiredTags' || key.startsWith('requiredTags['))
    .flatMap(([, messages]) => messages)
}
