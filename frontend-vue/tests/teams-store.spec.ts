import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useTeamsStore } from '@/stores/teams'

/**
 * A page can ask for teams before the shell has picked a project (the board does, on a
 * hard reload). That early call has nothing to fetch; what matters is that it does not
 * leave a settled promise behind that makes every later load() a no-op.
 */

/** happy-dom has no localStorage, and the remembering below is the point of these. */
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

let storage: Map<string, string>

beforeEach(() => {
  setActivePinia(createPinia())
  storage = installStorage()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('single-flight loaders', () => {
  it('fetches teams once a project is chosen, after an early call without one', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL) => new Response(JSON.stringify([]), { status: 200, headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    const teams = useTeamsStore()

    await teams.load()
    expect(fetchMock).not.toHaveBeenCalled()

    useOrganizationsStore().select('acme')
    useProjectsStore().select('WEB')
    await teams.load()

    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/orgs/acme/projects/WEB/teams'))).toBe(true)
  })

  it('fetches projects once an organization is chosen, after an early call without one', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL) => new Response(JSON.stringify([]), { status: 200, headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    const projects = useProjectsStore()

    await projects.load()
    expect(fetchMock).not.toHaveBeenCalled()

    useOrganizationsStore().select('acme')
    await projects.load()

    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/orgs/acme/projects'))).toBe(true)
  })
})

/**
 * A project key is unique inside an organization, not across the instance, and one person
 * may be in two organizations that both have a `WEB` — an owner in one, a stakeholder in
 * the other. Anything the client remembers per project key has to name the organization
 * too, or the second one inherits the first one's teams.
 */
describe('teams across organizations', () => {
  /** Both organizations have a project called WEB, each with its own default team. */
  function stubApi() {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      const acme = url.includes('/orgs/acme/')
      const body = url.includes('/teams')
        ? [{ id: acme ? 'acme-default' : 'dana-default', name: 'Default', isDefault: true }]
        : [{ id: acme ? 'acme-web' : 'dana-web', key: 'WEB', name: 'Web', isArchived: false }]
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  it('remembers a team per organization and project, not per project alone', async () => {
    stubApi()
    const organizations = useOrganizationsStore()
    const projects = useProjectsStore()
    const teams = useTeamsStore()

    organizations.select('acme')
    projects.select('WEB')
    await flushPromises()
    teams.select('acme-default')

    expect(storage.get('aictiq.team.acme.WEB')).toBe('acme-default')
    // The bare key would be the same string for the other organization's WEB.
    expect(storage.has('aictiq.team.WEB')).toBe(false)
  })

  it('reloads the team list when the organization changes under the same project key', async () => {
    const fetchMock = stubApi()
    const organizations = useOrganizationsStore()
    const projects = useProjectsStore()
    const teams = useTeamsStore()

    organizations.select('acme')
    projects.select('WEB')
    // The store loads itself from the watcher; letting that settle is what the shell does.
    await flushPromises()
    expect(teams.currentId).toBe('acme-default')

    // The project key does not change, so watching it alone would leave the other
    // organization's teams — and their ids — on screen.
    organizations.select('dana-co')
    projects.select('WEB')
    await flushPromises()

    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/orgs/dana-co/projects/WEB/teams'))).toBe(true)
    expect(teams.currentId).toBe('dana-default')
  })
})
