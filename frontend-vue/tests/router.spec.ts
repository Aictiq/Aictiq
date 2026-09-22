import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { router } from '@/router'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

const alice = {
  id: 'u1',
  email: 'alice@aictiq.local',
  firstName: 'Alice',
  lastName: 'Ng',
  fullName: 'Alice Ng',
  roles: ['User'],
  isAgent: false,
  avatarKey: null,
  timeZone: null,
}

/**
 * The guard reads the organization list as well when a URL names one. `stubSession` is
 * kept for the tests that never reach that branch.
 */
function stubApi(organizations: { id: string; slug: string; name: string }[]) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    const body = url.includes('/orgs')
      ? organizations.map((o) => ({ ...o, role: 'member', canOperateFactory: false }))
      : alice
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

/** The guard calls `session.load()`, which is the only thing that touches the network. */
function stubSession(signedIn: boolean) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        signedIn
          ? new Response(JSON.stringify(alice), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            })
          : new Response(JSON.stringify({ status: 401, title: 'Unauthorized' }), {
              status: 401,
              headers: { 'content-type': 'application/problem+json' },
            }),
      ),
    ),
  )
}

beforeEach(() => {
  setActivePinia(createPinia())
})

afterEach(async () => {
  vi.unstubAllGlobals()
})

describe('router', () => {
  it('resolves the routes the shell needs', () => {
    expect(router.resolve('/').name).toBe('home')
    expect(router.resolve('/login').name).toBe('login')
    expect(router.resolve('/register').name).toBe('register')
    expect(router.resolve('/onboarding').name).toBe('onboarding')
    expect(router.resolve('/forgot-password').name).toBe('forgot-password')
    expect(router.resolve('/o/acme/p/ACME/dashboard').name).toBe('project-dashboard')
    expect(router.resolve('/o/acme/p/ACME/items').name).toBe('project-items')
  })

  it('catches unknown paths instead of 404ing the server', () => {
    expect(router.resolve('/orgs/acme/nope').name).toBe('not-found')
  })

  it('marks the app routes as authenticated and the auth pages as public', () => {
    expect(router.resolve('/').meta.requiresAuth).toBe(true)
    expect(router.resolve('/login').meta.requiresAuth).toBe(false)
    expect(router.resolve('/login').meta.guestOnly).toBe(true)
  })

  it('sends an anonymous visitor to login and remembers where they were going', async () => {
    stubSession(false)

    await router.push('/')
    await router.isReady()

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.next).toBe('/')
  })

  it('keeps a signed-in visitor off the auth pages', async () => {
    stubSession(true)
    useSessionStore().set(alice)

    await router.push('/login')

    expect(router.currentRoute.value.path).toBe('/')
  })

  it('resolves every tab of the organization settings hub', () => {
    expect(router.resolve('/o/acme/settings/general').name).toBe('org-settings-general')
    expect(router.resolve('/o/acme/settings/members').name).toBe('org-settings-members')
    expect(router.resolve('/o/acme/settings/invitations').name).toBe('org-settings-invitations')
    expect(router.resolve('/o/acme/settings/agents').name).toBe('org-settings-agents')
    expect(router.resolve('/o/acme/settings/billing').name).toBe('org-settings-billing')
    expect(router.resolve('/o/acme/settings/integrations').name).toBe('org-settings-integrations')
    expect(router.resolve('/o/acme/settings/audit').name).toBe('org-settings-audit')
  })

  it('resolves every tab of the project settings hub', () => {
    const at = (tab: string) => router.resolve(`/o/acme/p/ACME/settings/${tab}`)

    expect(at('general').name).toBe('project-settings-general')
    expect(at('factory').name).toBe('project-settings-factory')
    expect(at('members').name).toBe('project-settings-members')
    expect(at('teams').name).toBe('project-settings-teams')
    expect(at('workflow').name).toBe('project-settings-workflow')
    expect(at('labels').name).toBe('project-settings-labels')
    expect(at('templates').name).toBe('project-settings-templates')
    expect(at('integrations').name).toBe('project-settings-integrations')
  })

  it('addresses a team through the project that owns it', () => {
    const resolved = router.resolve('/o/acme/p/ACME/settings/teams/t1')

    expect(resolved.name).toBe('team-settings')
    expect(resolved.params).toEqual({ slug: 'acme', projectKey: 'ACME', teamId: 't1' })
  })

  it('lands a bare settings URL on its first tab', async () => {
    stubSession(true)
    useSessionStore().set(alice)
    const organizations = useOrganizationsStore()
    organizations.organizations = [
      { id: 'o1', slug: 'acme', name: 'Acme', role: 'owner', canOperateFactory: true },
    ]
    organizations.status = 'ready'
    organizations.select('acme')

    await router.push('/o/acme/settings')
    expect(router.currentRoute.value.path).toBe('/o/acme/settings/general')

    await router.push('/o/acme/p/ACME/settings')
    expect(router.currentRoute.value.path).toBe('/o/acme/p/ACME/settings/general')

    await router.push('/settings')
    expect(router.currentRoute.value.path).toBe('/settings/profile')
  })

  it('marks only the screens that do not exist yet as unbuilt', () => {
    expect(router.resolve('/items').meta.soon).toBe(true)
    expect(router.resolve('/o/acme/settings/billing').meta.soon).toBeUndefined()
    expect(router.resolve('/o/acme/p/ACME/settings/templates').meta.soon).toBeUndefined()
    expect(router.resolve('/o/acme/p/ACME/settings/labels').meta.soon).toBeUndefined()
    expect(router.resolve('/o/acme/factory/playbooks').meta.soon).toBeUndefined()
  })

  it('carries the paths that predate the hub into it', async () => {
    stubSession(true)
    useSessionStore().set(alice)
    // The redirect targets are built from the organization the person is in, which is why
    // these are routes with a guard rather than static redirects.
    const organizations = useOrganizationsStore()
    organizations.organizations = [
      { id: 'o1', slug: 'acme', name: 'Acme', role: 'owner', canOperateFactory: true },
    ]
    organizations.status = 'ready'
    organizations.select('acme')

    await router.push('/settings/organization')
    expect(router.currentRoute.value.path).toBe('/o/acme/settings/general')

    await router.push('/members')
    expect(router.currentRoute.value.path).toBe('/o/acme/settings/members')

    await router.push('/projects/ACME/settings')
    expect(router.currentRoute.value.path).toBe('/o/acme/p/ACME/settings/general')

    await router.push('/projects/ACME/teams/t1/settings')
    expect(router.currentRoute.value.path).toBe('/o/acme/p/ACME/settings/teams/t1')
  })

  it('sends someone with no organization home, where the first one is created', async () => {
    stubSession(true)
    useSessionStore().set(alice)
    const organizations = useOrganizationsStore()
    organizations.organizations = []
    organizations.status = 'ready'
    organizations.select(null)

    await router.push('/settings/organization')

    expect(router.currentRoute.value.path).toBe('/')
  })

  it('moves the shell to the organization a pasted settings link names', async () => {
    stubSession(true)
    useSessionStore().set(alice)
    const organizations = useOrganizationsStore()
    organizations.organizations = [
      { id: 'o1', slug: 'acme', name: 'Acme', role: 'owner', canOperateFactory: true },
      { id: 'o2', slug: 'globex', name: 'Globex', role: 'member', canOperateFactory: true },
    ]
    organizations.status = 'ready'
    organizations.select('acme')

    await router.push('/o/globex/settings/members')

    expect(organizations.currentSlug).toBe('globex')
  })

  it('returns a signed-in visitor to the page they were bounced from', async () => {
    stubSession(true)
    useSessionStore().set(alice)

    await router.push('/login?next=/somewhere')

    expect(router.currentRoute.value.path).toBe('/somewhere')
  })
})

/**
 * A person can be in several organizations with a different standing in each, and links
 * to all of them land in the same browser. The URL decides which one the shell is in -
 * but only when it is one of theirs.
 */
describe('organization scope from the URL', () => {
  it('moves the shell to the organization a pasted link names', async () => {
    stubApi([
      { id: 'o1', slug: 'acme', name: 'Acme' },
      { id: 'o2', slug: 'dana-co', name: 'Dana & Co' },
    ])
    useSessionStore().set(alice)
    const organizations = useOrganizationsStore()
    await organizations.load()
    organizations.select('acme')
    // The router is a module singleton: start each of these from a route with no slug, or
    // a push to where the previous test ended is dropped as a duplicate navigation.
    await router.replace('/')

    await router.push('/o/dana-co/settings/general')

    expect(organizations.currentSlug).toBe('dana-co')
  })

  it('finds a membership that was accepted after the list was read', async () => {
    stubApi([{ id: 'o1', slug: 'acme', name: 'Acme' }])
    useSessionStore().set(alice)
    const organizations = useOrganizationsStore()
    await organizations.load()
    organizations.select('acme')
    // The router is a module singleton: start each of these from a route with no slug, or
    // a push to where the previous test ended is dropped as a duplicate navigation.
    await router.replace('/')

    // The invitation was accepted in another tab: the list read at boot cannot know.
    stubApi([
      { id: 'o1', slug: 'acme', name: 'Acme' },
      { id: 'o2', slug: 'dana-co', name: 'Dana & Co' },
    ])
    await router.push('/o/dana-co/settings/general')

    expect(organizations.currentSlug).toBe('dana-co')
  })

  it('leaves the shell where it is for an organization the visitor is not in', async () => {
    stubApi([{ id: 'o1', slug: 'acme', name: 'Acme' }])
    useSessionStore().set(alice)
    const organizations = useOrganizationsStore()
    await organizations.load()
    organizations.select('acme')
    // The router is a module singleton: start each of these from a route with no slug, or
    // a push to where the previous test ended is dropped as a duplicate navigation.
    await router.replace('/')

    await router.push('/o/globex/settings/general')

    // The page answers 404 by itself; what must not happen is the sidebar and the
    // switcher emptying because they now point at an organization with no membership.
    expect(organizations.currentSlug).toBe('acme')
  })
})
