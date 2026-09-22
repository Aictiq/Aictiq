import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'

import { listTeams, type Team } from '@/api/teams'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'

/**
 * The teams of the current project, and which one is being worked in.
 *
 * Stored per organization *and* project, like the project choice is stored per
 * organization: a team id means nothing outside the project that owns it, and the API
 * answers 404 to a team addressed through the wrong project. The organization is part of
 * the key because a project key is unique only inside one - two organizations may both
 * have a `WEB`, and one person may be in both. When nothing is remembered the store falls
 * back to the project's **default** team, which is exactly what the default is for.
 */

const STORAGE_PREFIX = 'aictiq.team.'

function storageKey(slug: string, projectKey: string) {
  return `${STORAGE_PREFIX}${slug}.${projectKey}`
}

function readStoredId(slug: string, projectKey: string): string | null {
  try {
    return localStorage.getItem(storageKey(slug, projectKey))
  } catch {
    return null
  }
}

function writeStoredId(slug: string, projectKey: string, teamId: string | null) {
  try {
    if (teamId === null) localStorage.removeItem(storageKey(slug, projectKey))
    else localStorage.setItem(storageKey(slug, projectKey), teamId)
  } catch {
    // The choice still applies for this session.
  }
}

export const useTeamsStore = defineStore('teams', () => {
  const organizations = useOrganizationsStore()
  const projects = useProjectsStore()

  const teams = ref<Team[]>([])
  const currentId = ref<string | null>(null)
  const status = ref<'unknown' | 'loading' | 'ready' | 'error'>('unknown')

  const current = computed(() => teams.value.find((t) => t.id === currentId.value) ?? null)

  function select(teamId: string | null) {
    currentId.value = teamId
    if (organizations.currentSlug && projects.currentKey) {
      writeStoredId(organizations.currentSlug, projects.currentKey, teamId)
    }
  }

  /** Falls back to the default team - the one a project always has, for exactly this. */
  function reconcile() {
    if (current.value) return
    select(teams.value.find((t) => t.isDefault)?.id ?? teams.value[0]?.id ?? null)
  }

  let loading: Promise<void> | null = null

  function load(): Promise<void> {
    loading ??= (async () => {
      const slug = organizations.currentSlug
      const projectKey = projects.currentKey
      if (!slug || !projectKey) {
        teams.value = []
        status.value = 'ready'
        return
      }

      status.value = 'loading'
      try {
        teams.value = await listTeams(slug, projectKey)
        currentId.value ??= readStoredId(slug, projectKey)
        status.value = 'ready'
        reconcile()
      } catch {
        status.value = 'error'
      }
      // Cleared in `finally` on the promise, never inside the body: the early return above
      // runs synchronously, and clearing there would happen *before* `??=` stores the
      // promise - leaving a settled promise cached, so every later load() skipped the fetch.
    })().finally(() => {
      loading = null
    })

    return loading
  }

  async function reload(): Promise<void> {
    status.value = 'unknown'
    await load()
  }

  function replace(updated: Team) {
    teams.value = teams.value
      // Promoting a team demotes the incumbent server-side, so the whole list is
      // re-derived rather than only the row that was written.
      .map((t) =>
        t.id === updated.id ? updated : updated.isDefault ? { ...t, isDefault: false } : t,
      )
      .sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.name.localeCompare(b.name))
  }

  function remove(teamId: string) {
    teams.value = teams.value.filter((t) => t.id !== teamId)
    if (currentId.value === teamId) currentId.value = null
    reconcile()
  }

  function clear() {
    teams.value = []
    status.value = 'unknown'
    currentId.value = null
  }

  // A team belongs to one project, so switching projects invalidates the list and the
  // selection together - and so does switching organizations, which the project key alone
  // does not report: two organizations can both have a `WEB`, and the teams behind it are
  // not the same teams.
  watch(
    () => [organizations.currentSlug, projects.currentKey] as const,
    ([slug, projectKey]) => {
      teams.value = []
      status.value = 'unknown'
      currentId.value = slug && projectKey ? readStoredId(slug, projectKey) : null
      if (slug && projectKey) void load()
    },
  )

  return { teams, currentId, current, status, select, load, reload, replace, remove, clear }
})
