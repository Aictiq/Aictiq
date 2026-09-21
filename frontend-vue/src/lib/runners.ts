import type { Runner } from '@/api/runners'

/**
 * The rules the Runners tab shows, kept out of the component so they can be tested: what
 * state a runner is in, and the exact command a person pastes on the machine.
 */

export type RunnerStatus = 'online' | 'offline' | 'never' | 'disabled'

export function runnerStatus(runner: Pick<Runner, 'isDisabled' | 'isOnline' | 'lastSeenAt'>): RunnerStatus {
  if (runner.isDisabled) return 'disabled'
  if (runner.isOnline) return 'online'
  // A runner that has never said hello is a registration nobody has used yet — a different
  // thing to fix from a machine that went quiet.
  return runner.lastSeenAt ? 'offline' : 'never'
}

export const runnerStatusLabel: Record<RunnerStatus, string> = {
  online: 'Online',
  offline: 'Offline',
  never: 'Waiting for first contact',
  disabled: 'Disabled',
}

/**
 * The command that registers this machine. The URL is the
 * page's own origin: the SPA is same-origin with the API, so the address in the browser bar
 * is the address a runner must reach.
 */
export function runnerRegisterCommand(origin: string, secret: string, name?: string): string {
  const base = `aictiq runner register --url ${origin.replace(/\/+$/, '')} --token ${secret}`
  return name ? `${base} --name ${shellQuote(name)}` : base
}

/** Quotes only when the shell would split or expand the value. */
function shellQuote(value: string): string {
  return /^[A-Za-z0-9._-]+$/.test(value) ? value : `'${value.replace(/'/g, `'\\''`)}'`
}
