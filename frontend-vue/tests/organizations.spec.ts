import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { OrganizationSummary } from '@/api/organizations'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * The store's job is to keep the current organization pointing at one the person is
 * actually a member of. The API is the authority - every org-scoped URL is checked
 * server-side - so what is tested here is that the client never *strands* itself: on a
 * stale stored slug, on a deletion, or on sign-out.
 */

const acme: OrganizationSummary = {
  id: 'o1',
  slug: 'acme',
  name: 'Acme',
  role: 'owner',
  canOperateFactory: true,
}
const globex: OrganizationSummary = {
  id: 'o2',
  slug: 'globex',
  name: 'Globex',
  role: 'member',
  canOperateFactory: true,
}

/**
 * happy-dom does not provide `localStorage`, so the store's persistence would silently
 * take its "storage unavailable" branch and none of the remembering below would be
 * tested. This is the browser API the environment is missing, supplied deliberately.
 */
function installStorage(): Map<string, string> {
  const entries = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => entries.get(key) ?? null,
    setItem: (key: string, value: string) => void entries.set(key, value),
    removeItem: (key: string) => void entries.delete(key),
    clear: () => entries.clear(),
  })
  return entries
}

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' },
  })
}

function stubList(organizations: OrganizationSummary[]) {
  const fetchMock = vi.fn(async () => jsonResponse(200, organizations))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

let storage: Map<string, string>

beforeEach(() => {
  setActivePinia(createPinia())
  storage = installStorage()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('organizations store', () => {
  it('selects the first organization when nothing was remembered', async () => {
    stubList([acme, globex])
    const store = useOrganizationsStore()

    await store.load()

    expect(store.current?.slug).toBe('acme')
    expect(store.isEmpty).toBe(false)
  })

  it('restores the remembered organization across a reload', async () => {
    storage.set('aictiq.org', 'globex')
    stubList([acme, globex])
    const store = useOrganizationsStore()

    await store.load()

    expect(store.current?.slug).toBe('globex')
  })

  it('falls back when the remembered organization is gone', async () => {
    // Deleted, or the membership was removed - neither is the user's doing, and leaving
    // the app pointed at nothing would be the worst of the available answers.
    storage.set('aictiq.org', 'departed')
    stubList([acme])
    const store = useOrganizationsStore()

    await store.load()

    expect(store.current?.slug).toBe('acme')
    expect(storage.get('aictiq.org')).toBe('acme')
  })

  it('reports the first-run state rather than an empty selection', async () => {
    stubList([])
    const store = useOrganizationsStore()

    await store.load()

    expect(store.isEmpty).toBe(true)
    expect(store.current).toBeNull()
  })

  it('loads once for concurrent callers', async () => {
    const fetchMock = stubList([acme])
    const store = useOrganizationsStore()

    await Promise.all([store.load(), store.load(), store.load()])

    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('keeps what it had when the list cannot be fetched', async () => {
    stubList([acme, globex])
    const store = useOrganizationsStore()
    await store.load()

    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(500, { title: 'Boom' })),
    )
    await store.reload()

    // The shell renders without a switcher rather than blocking on a failed call.
    expect(store.status).toBe('error')
    expect(store.current?.slug).toBe('acme')
  })

  it('switches to the newly created organization', async () => {
    stubList([acme])
    const store = useOrganizationsStore()
    await store.load()

    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(201, {
          ...globex,
          plan: 'self_hosted',
          timeZone: 'UTC',
          weekStart: 'monday',
          createdAt: '2026-01-01T00:00:00Z',
          version: 1,
        }),
      ),
    )
    await store.create({ name: 'Globex' })

    expect(store.current?.slug).toBe('globex')
    expect(store.organizations).toHaveLength(2)
  })

  it('moves on when the current organization is removed', async () => {
    stubList([acme, globex])
    const store = useOrganizationsStore()
    await store.load()
    store.select('globex')

    store.remove('globex')

    expect(store.current?.slug).toBe('acme')
    expect(store.organizations.map((o) => o.slug)).toEqual(['acme'])
  })

  it('forgets everything on sign-out, remembered slug included', async () => {
    stubList([acme])
    const store = useOrganizationsStore()
    await store.load()

    store.clear()

    // The next person at this browser is not them.
    expect(store.organizations).toEqual([])
    expect(store.current).toBeNull()
    expect(storage.has('aictiq.org')).toBe(false)
  })

  it('survives storage being blocked', async () => {
    // A private window, or a browser set to refuse site data: every accessor throws
    // rather than returning null, and the app still has to render.
    const blocked = () => {
      throw new Error('The operation is insecure.')
    }
    vi.stubGlobal('localStorage', {
      getItem: blocked,
      setItem: blocked,
      removeItem: blocked,
      clear: blocked,
    })
    stubList([acme])

    const store = useOrganizationsStore()
    await store.load()

    expect(store.current?.slug).toBe('acme')
  })
})
