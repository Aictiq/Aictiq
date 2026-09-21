import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'

import { createProject, listProjects, type CreateProjectBody, type Project } from '@/api/projects'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * The projects of the current organization, and which one is being worked in.
 *
 * The selection is stored per organization, because it means nothing outside one: someone
 * who switches to another organization must not land in a project key that belongs to the
 * one they left. Like the organization choice it is a *convenience*, never a permission —
 * every request names the project in its URL and the API answers 404 if the caller cannot
 * see it, so a stale or tampered value fails closed.
 */

const STORAGE_PREFIX = 'aictiq.project.'

function readStoredKey(slug: string): string | null {
  try {
    return localStorage.getItem(STORAGE_PREFIX + slug)
  } catch {
    return null
  }
}

function writeStoredKey(slug: string, key: string | null) {
  try {
    if (key === null) localStorage.removeItem(STORAGE_PREFIX + slug)
    else localStorage.setItem(STORAGE_PREFIX + slug, key)
  } catch {
    // The choice still applies for this session.
  }
}

export const useProjectsStore = defineStore('projects', () => {
  const organizations = useOrganizationsStore()

  const projects = ref<Project[]>([])
  const currentKey = ref<string | null>(null)
  const status = ref<'unknown' | 'loading' | 'ready' | 'error'>('unknown')

  const current = computed(() => projects.value.find((p) => p.key === currentKey.value) ?? null)
  const isResolved = computed(() => status.value === 'ready' || status.value === 'error')
  /** No projects at all — the first-run state the create dialog exists for. */
  const isEmpty = computed(() => status.value === 'ready' && projects.value.length === 0)

  function select(key: string | null) {
    currentKey.value = key
    if (organizations.currentSlug) writeStoredKey(organizations.currentSlug, key)
  }

  /** Keeps the selection pointing at a project that still exists and is still visible. */
  function reconcile() {
    if (current.value) return
    select(projects.value.find((p) => !p.isArchived)?.key ?? null)
  }

  let loading: Promise<void> | null = null

  function load(): Promise<void> {
    loading ??= (async () => {
      const slug = organizations.currentSlug
      if (!slug) {
        projects.value = []
        status.value = 'ready'
        return
      }

      status.value = 'loading'
      try {
        projects.value = await listProjects(slug)
        currentKey.value ??= readStoredKey(slug)
        status.value = 'ready'
        reconcile()
      } catch {
        // The shell renders without a project list rather than blocking on this.
        status.value = 'error'
      }
      // Cleared in `finally` on the promise, never inside the body: the early return above
      // runs synchronously, and clearing there would happen *before* `??=` stores the
      // promise — leaving a settled promise cached, so every later load() skipped the fetch.
    })().finally(() => {
      loading = null
    })

    return loading
  }

  async function reload(): Promise<void> {
    status.value = 'unknown'
    await load()
  }

  async function create(body: CreateProjectBody): Promise<Project> {
    const slug = organizations.currentSlug
    if (!slug) throw new Error('No organization is selected.')

    const created = await createProject(slug, body)
    projects.value = [...projects.value, created].sort((a, b) => a.name.localeCompare(b.name))
    // Creating one is an implicit choice to work in it.
    select(created.key)
    status.value = 'ready'
    return created
  }

  /** After an edit or an archive, so the sidebar does not keep showing the old state. */
  function replace(updated: Project) {
    projects.value = projects.value
      .map((p) => (p.id === updated.id ? updated : p))
      .sort((a, b) => a.name.localeCompare(b.name))
    if (updated.isArchived && currentKey.value === updated.key) reconcile()
  }

  function remove(key: string) {
    projects.value = projects.value.filter((p) => p.key !== key)
    if (currentKey.value === key) select(null)
    reconcile()
  }

  function clear() {
    projects.value = []
    status.value = 'unknown'
    currentKey.value = null
  }

  // Switching organizations invalidates everything here, including the selection: a
  // project key is only meaningful inside the organization that owns it.
  watch(
    () => organizations.currentSlug,
    (slug) => {
      projects.value = []
      status.value = 'unknown'
      currentKey.value = slug ? readStoredKey(slug) : null
      if (slug) void load()
    },
  )

  return {
    projects,
    currentKey,
    current,
    status,
    isResolved,
    isEmpty,
    select,
    load,
    reload,
    create,
    replace,
    remove,
    clear,
  }
})
