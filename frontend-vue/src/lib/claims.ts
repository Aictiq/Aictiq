/**
 * Claim presentation rules, kept out of the component because they decide what a
 * stakeholder is told and are worth asserting directly.
 *
 * A claim is a lease, not an assignment. It stays alive only while its holder sends
 * heartbeats; once the heartbeat is older than the server's `Claims:StaleAfterMinutes`
 * (30 by default) anyone may claim the item again. So the screen has three things to say
 * apart, not two: nobody holds this, someone is working on it, and someone *was* working
 * on it and has probably died.
 */

/** Mirrors the API's `Claims:StaleAfterMinutes` default. */
export const staleAfterMinutes = 30

export type ClaimStatus = 'none' | 'live' | 'stale'

export interface ClaimLike {
  claimedBy: string | null
  claimHeartbeatAt: string | null
}

export function claimStatus(
  item: ClaimLike | null | undefined,
  now: Date = new Date(),
  staleMinutes = staleAfterMinutes,
): ClaimStatus {
  if (!item?.claimedBy) return 'none'
  // A claim with no heartbeat at all is treated as live: the claim endpoint writes one in
  // the same statement, so an absent value means an old row rather than a dead agent, and
  // offering to break a claim on that basis would be wrong.
  if (!item.claimHeartbeatAt) return 'live'
  const beat = new Date(item.claimHeartbeatAt).getTime()
  if (Number.isNaN(beat)) return 'live'
  return now.getTime() - beat > staleMinutes * 60_000 ? 'stale' : 'live'
}

/** "just now", "4 min ago", "3 h ago", "2 d ago" — short enough to sit inside a banner. */
export function since(value: string | null | undefined, now: Date = new Date()): string {
  if (!value) return ''
  const then = new Date(value).getTime()
  if (Number.isNaN(then)) return ''
  const seconds = Math.max(0, Math.round((now.getTime() - then) / 1000))
  if (seconds < 45) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} h ago`
  return `${Math.round(hours / 24)} d ago`
}

/**
 * Who may break a claim. The holder always may — releasing your own lease is not a
 * privilege. Anyone else needs project Admin, which is the same rule the API applies; this
 * only decides whether to draw the button, and a 403 is still the real answer.
 */
export function canRelease(
  status: ClaimStatus,
  options: { claimedBy: string | null; currentUserId: string | null | undefined; projectRole?: string | null },
): boolean {
  if (status === 'none') return false
  if (options.claimedBy && options.claimedBy === options.currentUserId) return true
  return options.projectRole === 'admin'
}
