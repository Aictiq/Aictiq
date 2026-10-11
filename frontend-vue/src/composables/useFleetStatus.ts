import { computed, reactive, toValue, watch, type MaybeRefOrGetter } from 'vue'

import { getFleetStatus, type FleetStatus } from '@/api/fleet'

/**
 * Whether fleet mode is in force for an organization, read once and shared by every screen
 * that grows a field for it (the Runners roster, the playbook editor, the rule dialog, Start
 * run, the settings nav).
 *
 * Every answer short of an active fleet - the read failed, an older server answered 404, the
 * instance is self-hosted - means "inactive", and inactive means each of those screens looks
 * and behaves exactly as it did before fleet mode existed. Nothing is ever sent on a guess.
 */

/** Long enough to share one read across a dialog opened twice; short enough to pick up new tags. */
const freshForMs = 60_000

interface Entry {
  status: FleetStatus | null
  at: number
  pending: Promise<void> | null
}

const cache = reactive(new Map<string, Entry>())

async function fetchStatus(slug: string, entry: Entry): Promise<void> {
  try {
    const status = await getFleetStatus(slug)
    // A response that is not a status at all (a stubbed or proxied route) reads as hidden.
    entry.status =
      status && typeof status === 'object' && 'availability' in status
        ? { ...status, knownTags: status.knownTags ?? [] }
        : null
    entry.at = Date.now()
  } catch {
    // Not remembered: the next screen asks again rather than hiding fleet mode for a minute.
    entry.status = null
    entry.at = 0
  } finally {
    entry.pending = null
  }
}

/** Reads the status unless a fresh one is cached; `force` reads it regardless (after a save). */
export function loadFleetStatus(slug: string, force = false): Promise<void> {
  if (!slug) return Promise.resolve()
  let entry = cache.get(slug)
  if (!entry) {
    cache.set(slug, { status: null, at: 0, pending: null })
    entry = cache.get(slug)!
  }
  if (entry.pending) return entry.pending
  if (!force && entry.at > 0 && Date.now() - entry.at < freshForMs) return Promise.resolve()
  entry.pending = fetchStatus(slug, entry)
  return entry.pending
}

/** Forgets every cached status; for tests, and for a session that changes hands. */
export function clearFleetStatusCache() {
  cache.clear()
}

export function useFleetStatus(slug: MaybeRefOrGetter<string>) {
  watch(
    () => toValue(slug),
    (value) => void loadFleetStatus(value),
    { immediate: true },
  )

  const status = computed(() => cache.get(toValue(slug))?.status ?? null)

  return {
    status,
    /** Fleet rules are in force: show tags, required tags and the queue panel. */
    active: computed(() => status.value?.active === true),
    /** Enabled, but the plan lapsed: say so where fleet controls would be. */
    downgraded: computed(() => status.value?.downgraded === true),
    /** Self-hosted, or not known yet: no trace of fleet mode anywhere. */
    hidden: computed(() => (status.value?.availability ?? 'hidden') === 'hidden'),
    knownTags: computed(() => status.value?.knownTags ?? []),
    refresh: () => loadFleetStatus(toValue(slug), true),
  }
}
