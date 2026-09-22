import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

import {
  createOrganization,
  listOrganizations,
  type CreateOrganizationBody,
  type OrganizationSummary,
} from '@/api/organizations'

/**
 * Which organizations this person belongs to, and which one they are acting in.
 *
 * The current organization is a *client* choice that the API confirms: every
 * organization-scoped request names it in the URL, and the API answers 404 if the person
 * is not a member - so a stale or tampered stored slug fails closed. That is why it is
 * safe to keep the last choice in `localStorage`; it is a convenience, never a
 * permission.
 */

const STORAGE_KEY = 'aictiq.org'

function readStoredSlug(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY)
  } catch {
    // A private window or blocked storage: the fall-back below still picks something.
    return null
  }
}

function writeStoredSlug(slug: string | null) {
  try {
    if (slug === null) localStorage.removeItem(STORAGE_KEY)
    else localStorage.setItem(STORAGE_KEY, slug)
  } catch {
    // The choice still applies for this session.
  }
}

export const useOrganizationsStore = defineStore('organizations', () => {
  const organizations = ref<OrganizationSummary[]>([])
  const currentSlug = ref<string | null>(readStoredSlug())
  const status = ref<'unknown' | 'loading' | 'ready' | 'error'>('unknown')

  const current = computed(
    () => organizations.value.find((o) => o.slug === currentSlug.value) ?? null,
  )
  const isResolved = computed(() => status.value === 'ready' || status.value === 'error')
  /** No organizations at all - the first-run state the create dialog exists for. */
  const isEmpty = computed(() => status.value === 'ready' && organizations.value.length === 0)

  function select(slug: string | null) {
    currentSlug.value = slug
    writeStoredSlug(slug)
  }

  /**
   * Keeps the selection pointing at something real. A slug can go stale in ways that are
   * not the user's fault - the organization was deleted, or their membership was
   * removed - and the honest response is to move them to one they still have rather than
   * to leave the app pointed at nothing.
   */
  function reconcile() {
    if (current.value) return
    select(organizations.value[0]?.slug ?? null)
  }

  let loading: Promise<void> | null = null

  /** Single-flight, like the session: the shell and a settings page both want this. */
  function load(): Promise<void> {
    loading ??= (async () => {
      status.value = 'loading'
      try {
        organizations.value = await listOrganizations()
        status.value = 'ready'
        reconcile()
      } catch {
        // Leave whatever was already loaded in place; the shell renders without a
        // switcher rather than blocking the whole app on this call.
        status.value = 'error'
      } finally {
        loading = null
      }
    })()

    return loading
  }

  async function reload(): Promise<void> {
    status.value = 'unknown'
    await load()
  }

  async function create(body: CreateOrganizationBody): Promise<OrganizationSummary> {
    const created = await createOrganization(body)
    const summary: OrganizationSummary = {
      id: created.id,
      slug: created.slug,
      name: created.name,
      role: created.role,
      canOperateFactory: created.canOperateFactory,
    }

    organizations.value = [...organizations.value, summary].sort((a, b) =>
      a.name.localeCompare(b.name),
    )
    // Creating one is an implicit choice to work in it.
    select(created.slug)
    status.value = 'ready'
    return summary
  }

  /** After a rename, so the switcher does not keep showing the old name. */
  function replace(updated: OrganizationSummary) {
    organizations.value = organizations.value
      .map((o) => (o.id === updated.id ? updated : o))
      .sort((a, b) => a.name.localeCompare(b.name))
  }

  function remove(slug: string) {
    organizations.value = organizations.value.filter((o) => o.slug !== slug)
    if (currentSlug.value === slug) {
      select(null)
    }
    reconcile()
  }

  /** On sign-out. The stored slug goes too - the next person on this browser is not them. */
  function clear() {
    organizations.value = []
    status.value = 'unknown'
    select(null)
  }

  return {
    organizations,
    currentSlug,
    current,
    status,
    isResolved,
    isEmpty,
    load,
    reload,
    select,
    create,
    replace,
    remove,
    clear,
  }
})
