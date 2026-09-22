import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import GettingStartedPanel from '@/components/onboarding/GettingStartedPanel.vue'
import ProductTour from '@/components/onboarding/ProductTour.vue'
import {
  getOnboarding,
  updateOnboarding,
  type OnboardingPreferences,
  type UpdateOnboardingBody,
} from '@/api/onboarding'
import {
  readChecklistHint,
  tourSteps,
  writeChecklistHint,
  type TourContext,
} from '@/lib/onboarding'
import { useOnboardingStore } from '@/stores/onboarding'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'
import { ApiError, ConflictError } from '@/utils/api'

// The store and the components go through this module for every server conversation;
// mocking it keeps the tests off ofetch's URL/body plumbing.
vi.mock('@/api/onboarding', () => ({
  getOnboarding: vi.fn(),
  updateOnboarding: vi.fn(),
}))

const getMock = vi.mocked(getOnboarding)
const patchMock = vi.mocked(updateOnboarding)

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

const record = (overrides: Partial<OnboardingPreferences> = {}): OnboardingPreferences => ({
  tourVersion: 1,
  status: 'not_started',
  lastStepId: null,
  completedAt: null,
  updatedAt: '2026-09-20T10:00:00Z',
  version: 0,
  ...overrides,
})

function lastPatch(): UpdateOnboardingBody {
  const call = patchMock.mock.lastCall
  if (!call) throw new Error('expected a PATCH')
  return call[0]
}

// happy-dom ships no storage in this setup, so a Map stands in for the browser's.
function stubStorage() {
  const store = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => void store.set(key, value),
    removeItem: (key: string) => void store.delete(key),
  })
}

const passthrough = { template: '<div><slot /></div>' }

const stakeholder: TourContext = {
  hasOrganization: true,
  canOperateFactory: false,
  orgRole: 'member',
  orgSlug: 'acme',
  projectKey: 'PROJ',
  hasProject: true,
  hasTeam: true,
}

const operator: TourContext = { ...stakeholder, canOperateFactory: true, orgRole: 'admin' }

beforeEach(() => {
  setActivePinia(createPinia())
  stubStorage()
  getMock.mockReset()
  patchMock.mockReset()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('onboarding store', () => {
  it('reads a fresh account as not_started and writes nothing on the way', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record())

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()

    expect(onboarding.loaded).toBe(true)
    expect(onboarding.status).toBe('not_started')
    expect(onboarding.version).toBe(0)
    expect(patchMock).not.toHaveBeenCalled()
  })

  it('does not even ask for an agent, and not for a signed-out tab either', async () => {
    useSessionStore().set({ ...alice, isAgent: true })
    await useOnboardingStore().bootstrap()
    expect(getMock).not.toHaveBeenCalled()

    setActivePinia(createPinia())
    await useOnboardingStore().bootstrap()
    expect(getMock).not.toHaveBeenCalled()
  })

  it('loads once for concurrent callers', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record())

    const onboarding = useOnboardingStore()
    await Promise.all([onboarding.bootstrap(), onboarding.bootstrap()])

    expect(getMock).toHaveBeenCalledOnce()
  })

  it('marks a failed read without throwing, so the app stays usable', async () => {
    useSessionStore().set(alice)
    getMock.mockRejectedValue(new ApiError(500, 'Boom.'))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()

    expect(onboarding.loaded).toBe(false)
    expect(onboarding.loadFailed).toBe(true)
  })

  it('starts the tour by writing in_progress at the first step', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record())
    patchMock.mockImplementation(async (body) =>
      record({ status: body.status, lastStepId: body.lastStepId ?? null, version: 5 }),
    )

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.startTour()
    await flushPromises()

    expect(onboarding.tourActive).toBe(true)
    expect(lastPatch()).toMatchObject({ status: 'in_progress', lastStepId: 'navigation', version: 0 })
    expect(onboarding.version).toBe(5)
  })

  it('replays a finished tour locally and leaves the server record alone', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record({ status: 'completed', version: 7, completedAt: '2026-09-01T09:00:00Z' }))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.startTour()
    await flushPromises()

    expect(patchMock).not.toHaveBeenCalled()
    expect(onboarding.tourActive).toBe(true)
    expect(onboarding.status).toBe('completed')
  })

  it('finishes the tour at the last step', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record())
    patchMock.mockImplementation(async (body) => record({ status: body.status, version: 6 }))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.startTour('follow-up')
    onboarding.next()
    await flushPromises()

    expect(onboarding.status).toBe('completed')
    expect(onboarding.tourActive).toBe(false)
    expect(lastPatch()).toMatchObject({ status: 'completed', lastStepId: null })
  })

  it('defers with Later and keeps the step for the resume entry', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record())
    patchMock.mockResolvedValue(record({ status: 'deferred', lastStepId: 'navigation', version: 3 }))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.startTour()
    await flushPromises()
    await onboarding.defer()

    expect(onboarding.tourActive).toBe(false)
    expect(lastPatch()).toMatchObject({ status: 'deferred', lastStepId: 'navigation' })
    expect(onboarding.resumeEligible).toBe(true)
  })

  it('skips with dismissal and forgets the step', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record())
    patchMock.mockResolvedValueOnce(record({ status: 'in_progress', lastStepId: 'navigation', version: 3 }))
    patchMock.mockResolvedValueOnce(record({ status: 'dismissed', version: 4 }))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.startTour()
    await flushPromises()
    await onboarding.dismiss()

    expect(lastPatch()).toMatchObject({ status: 'dismissed', lastStepId: null, version: 3 })
    expect(onboarding.resumeEligible).toBe(false)
  })

  it('leaves a terminal record alone when a replay is skipped or finished', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record({ status: 'completed', completedAt: '2026-09-01T09:00:00Z', version: 7 }))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()

    // Replaying a finished tour is a local session: neither walking it nor leaving it
    // may trade the recorded completion (and its date) for something lesser.
    onboarding.startTour()
    onboarding.next()
    await onboarding.dismiss()
    onboarding.startTour()
    await onboarding.complete()
    await onboarding.defer()

    expect(patchMock).not.toHaveBeenCalled()
    expect(onboarding.status).toBe('completed')
    expect(onboarding.version).toBe(7)
  })

  it('adopts the server record on a conflict, and a terminal server state ends the tour', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValueOnce(record())
    patchMock.mockRejectedValueOnce(new ConflictError(409, 'Conflict.'))
    getMock.mockResolvedValueOnce(record({ status: 'completed', version: 9, completedAt: '2026-09-01T09:00:00Z' }))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.startTour()
    await flushPromises()
    await onboarding.dismiss()
    await flushPromises()

    expect(getMock).toHaveBeenCalledTimes(2)
    expect(onboarding.status).toBe('completed')
    expect(onboarding.version).toBe(9)
    expect(onboarding.tourActive).toBe(false)
    expect(onboarding.loadFailed).toBe(false)
  })

  it('keeps local state when a save fails and lets Get started offer Retry', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record())
    patchMock.mockRejectedValue(new ApiError(500, 'Boom.'))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.startTour()
    await flushPromises()
    await onboarding.dismiss()

    expect(onboarding.loadFailed).toBe(true)
  })

  it('offers the welcome only once a record has been read', () => {
    const onboarding = useOnboardingStore()
    onboarding.maybeShowWelcome()

    expect(onboarding.welcomeOpen).toBe(false)
  })

  it('clears everything on sign-out, including the once-per-tab welcome', async () => {
    useSessionStore().set(alice)
    getMock.mockResolvedValue(record({ status: 'dismissed', version: 4 }))

    const onboarding = useOnboardingStore()
    await onboarding.bootstrap()
    onboarding.clear()

    expect(onboarding.status).toBe('not_started')
    expect(onboarding.version).toBe(0)
    expect(onboarding.loaded).toBe(false)
    expect(onboarding.welcomeOpen).toBe(false)
    expect(onboarding.tourActive).toBe(false)
  })
})

describe('tour steps', () => {
  it('gives stakeholders the shared steps and skips the operator-only ones', () => {
    const steps = tourSteps(stakeholder)

    expect(steps).toHaveLength(8)
    expect(steps.map((step) => step.id)).not.toContain('runner-setup')
    expect(steps.map((step) => step.id)).not.toContain('handoff')
  })

  it('walks operators through the whole sequence in order', () => {
    expect(tourSteps(operator).map((step) => step.id)).toEqual([
      'navigation',
      'organization',
      'project-team',
      'board',
      'prepare-item',
      'project-knowledge',
      'factory-concepts',
      'runner-setup',
      'handoff',
      'follow-up',
    ])
  })

  it('anchors operators at the factory and hands stakeholders a card instead', () => {
    const concepts = (ctx: TourContext) => tourSteps(ctx).find((step) => step.id === 'factory-concepts')!

    expect(concepts(operator).anchor(operator)).toBe('factory-link')
    expect(concepts(stakeholder).anchor(stakeholder)).toBeNull()
  })
})

describe('the checklist memory', () => {
  it('round-trips per user, organization and project', () => {
    writeChecklistHint('u1', 'acme', 'PROJ', { itemId: 'PROJ-12', reviewed: false })

    expect(localStorage.getItem('aictiq.gs.u1.acme.PROJ')).toBeTruthy()
    expect(readChecklistHint('u1', 'acme', 'PROJ')).toEqual({ itemId: 'PROJ-12', reviewed: false })
    expect(readChecklistHint('u1', 'acme', 'OTHER')).toEqual({ itemId: null, reviewed: false })
  })

  it('reads an empty hint from an unreadable slot rather than failing', () => {
    localStorage.setItem('aictiq.gs.u1.acme.PROJ', '{not json')

    expect(readChecklistHint('u1', 'acme', 'PROJ')).toEqual({ itemId: null, reviewed: false })
  })
})

describe('product tour component', () => {
  it('greets with the welcome copy and defers on Later', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    getMock.mockResolvedValue(record())
    patchMock.mockResolvedValue(record({ status: 'deferred', version: 3 }))

    const onboarding = useOnboardingStore()
    onboarding.welcomeOpen = true

    const wrapper = mount(ProductTour, {
      global: { plugins: [pinia], stubs: { Dialog: passthrough, DialogContent: passthrough, DialogHeader: passthrough, DialogTitle: passthrough, DialogDescription: passthrough } },
    })

    expect(wrapper.text()).toContain('Welcome to Aictiq')
    expect(wrapper.text()).toContain('Find your way around, prepare a ticket')

    const later = wrapper.findAll('button').find((button) => button.text() === 'Later')!
    await later.trigger('click')
    await flushPromises()

    expect(onboarding.welcomeOpen).toBe(false)
    expect(lastPatch()).toMatchObject({ status: 'deferred' })
    wrapper.unmount()
  })

  it('falls back to a readable card with Retry when the anchor never appears', async () => {
    vi.useFakeTimers()
    try {
      const pinia = createPinia()
      setActivePinia(pinia)
      patchMock.mockResolvedValue(record({ status: 'in_progress', version: 4 }))

      const onboarding = useOnboardingStore()
      onboarding.tourActive = true
      // `navigation` anchors on the sidebar, which this bare mount does not render.
      onboarding.currentStepId = 'navigation'

      const wrapper = mount(ProductTour, { global: { plugins: [pinia] } })
      expect(wrapper.text()).not.toContain('Retry')

      // The wait is bounded: five seconds, then the card says so instead of spinning.
      await vi.advanceTimersByTimeAsync(5200)

      expect(wrapper.text()).toContain('Find your way around')
      expect(wrapper.text()).toContain('Retry')
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('shows progress and advances with Next', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    patchMock.mockResolvedValue(record({ status: 'in_progress', version: 4 }))

    const onboarding = useOnboardingStore()
    onboarding.tourActive = true
    onboarding.currentStepId = 'navigation'

    const wrapper = mount(ProductTour, { global: { plugins: [pinia] } })

    expect(wrapper.text()).toContain('Step 1 of')
    expect(wrapper.text()).toContain('Find your way around')

    const next = wrapper.findAll('button').find((button) => button.text() === 'Next')!
    await next.trigger('click')
    await flushPromises()

    expect(onboarding.currentStepId).toBe('organization')
    expect(wrapper.text()).toContain('Step 2 of')
    wrapper.unmount()
  })
})

describe('getting started panel', () => {
  it('reports the honest no-context state: needs setup, needs an admin, and the review button', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    useSessionStore().set(alice)
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify([]), { status: 200, headers: { 'content-type': 'application/json' } }),
      ),
    )

    const onboarding = useOnboardingStore()
    onboarding.checklistOpen = true

    const wrapper = mount(GettingStartedPanel, {
      global: {
        plugins: [pinia],
        stubs: {
          Sheet: passthrough,
          SheetContent: passthrough,
          SheetHeader: passthrough,
          SheetTitle: passthrough,
          SheetDescription: passthrough,
        },
      },
    })
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('Get started')
    expect(text).toContain('Find your way')
    expect(text).toContain('Prepare AI work')
    expect(text).toContain('Hand off and review')
    expect(text).toContain('Needs setup')
    expect(text).toContain('Needs an admin')
    // Review is the one acknowledgement the panel refuses to assume.
    expect(text).toContain('I reviewed the result')
    wrapper.unmount()
  })

  it('only counts an item the API confirms, and remembers it per project', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    useSessionStore().set(alice)

    const organizations = useOrganizationsStore()
    organizations.organizations = [
      { id: 'o1', name: 'Acme', slug: 'acme', role: 'admin', canOperateFactory: true },
    ] as never
    organizations.currentSlug = 'acme'
    const projectsStore = useProjectsStore()
    projectsStore.projects = [{ id: 'p1', key: 'PROJ', name: 'Proj', isArchived: false }] as never
    projectsStore.currentKey = 'PROJ'

    // Every other check answers with an empty list; the item is the only interesting one.
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes('/items/PROJ-404')) {
          return new Response(JSON.stringify({ title: 'Not found.' }), {
            status: 404,
            headers: { 'content-type': 'application/problem+json' },
          })
        }
        if (url.includes('/items/PROJ-7')) {
          return new Response(JSON.stringify({ key: 'PROJ-7', title: 'Fix the importer' }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        })
      }),
    )

    const onboarding = useOnboardingStore()
    onboarding.checklistOpen = true

    const wrapper = mount(GettingStartedPanel, {
      global: {
        plugins: [pinia],
        stubs: {
          Sheet: passthrough,
          SheetContent: passthrough,
          SheetHeader: passthrough,
          SheetTitle: passthrough,
          SheetDescription: passthrough,
        },
      },
    })
    await flushPromises()

    const field = () => wrapper.find('input')
    const submit = () => wrapper.find('form').trigger('submit')

    // A key nobody can open is not a selection, and nothing is written for it.
    await field().setValue('proj-404')
    await submit()
    await flushPromises()
    expect(wrapper.text()).toContain('No item PROJ-404 you can open.')
    expect(localStorage.getItem('aictiq.gs.u1.acme.PROJ')).toBeNull()

    await field().setValue('PROJ-7')
    await submit()
    await flushPromises()

    expect(wrapper.text()).toContain('PROJ-7 - Fix the importer')
    expect(readChecklistHint('u1', 'acme', 'PROJ').itemId).toBe('PROJ-7')
    wrapper.unmount()
  })
})
