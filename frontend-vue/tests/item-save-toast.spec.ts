import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import ItemDetail from '@/components/items/ItemDetail.vue'
import { useToast } from '@/composables/useToast'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

/**
 * Saving an item used to be silent: the row of buttons looked the same before and after, so
 * the only way to tell a save from a no-op was to leave and come back. It now says so, and
 * the buttons are only offered while there is in fact something to save.
 */
const toast = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
  info: vi.fn(),
}))
vi.mock('vue-sonner', () => ({
  toast: Object.assign((...args: unknown[]) => toast.info(...args), {
    success: toast.success,
    error: toast.error,
  }),
}))

const item = {
  id: 'i1',
  key: 'PROJ-1',
  type: 'story',
  title: 'Crash on save',
  stateId: 's1',
  descriptionMarkdown: 'Steps',
  descriptionHtml: '',
  stateCategory: 'active',
  priority: 'medium',
  assigneeId: null,
  teamId: null,
  sprintId: null,
  parentId: null,
  points: null,
  estimateHours: null,
  remainingHours: null,
  completedHours: null,
  dueDate: null,
  version: 4,
  labels: [],
  updatedAt: '2026-09-01T00:00:00Z',
  isWatching: false,
  watcherCount: 0,
  claimedBy: null,
  claimedAt: null,
  claimHeartbeatAt: null,
  rollup: {
    totalCount: 0,
    completedCount: 0,
    pointsTotal: 0,
    pointsCompleted: 0,
    remainingHours: 0,
  },
}

const project = {
  id: 'pr1',
  key: 'PROJ',
  name: 'Prototype',
  description: null,
  visibility: 'organization',
  icon: null,
  color: null,
  isArchived: false,
  createdAt: '2026-01-01T00:00:00Z',
  role: 'member',
  version: 1,
}

/** The save itself, so a test can decide whether the server accepts it. */
let patch: () => Response

function stubFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? ''
      if ((init?.method ?? 'GET') !== 'GET') return patch()
      const table: [RegExp, unknown][] = [
        [/\/items\/PROJ-1\/runs$/, { items: [], page: 1, pageSize: 25, totalCount: 0 }],
        [/\/items\/PROJ-1$/, item],
        [/\/items\/PROJ-1\/comments$/, { items: [], page: 1, pageSize: 50, totalCount: 0 }],
        [/\/items\/PROJ-1\/history$/, { items: [], page: 1, pageSize: 50, total: 0 }],
        [/\/items\/PROJ-1\/relations$/, []],
        [/\/items\/PROJ-1\/links$/, []],
        [/\/items\/PROJ-1\/watch$/, []],
        [/\/items\/PROJ-1\/children$/, []],
        [/\/workflows$/, []],
        [/\/projects\/PROJ\/members$/, []],
        [/\/projects\/PROJ$/, project],
        [/\/github/, []],
      ]
      for (const [pattern, body] of table) {
        if (pattern.test(url)) {
          return new Response(JSON.stringify(body), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
      }
      return new Response(JSON.stringify({ title: 'Not found', status: 404 }), {
        status: 404,
        headers: { 'content-type': 'application/problem+json' },
      })
    }),
  )
}

async function mountDetail(itemKey = 'PROJ-1') {
  stubFetch()
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      {
        path: '/o/:slug/p/:projectKey/items/:itemKey',
        name: 'item-detail',
        component: { template: '<div />' },
      },
      { path: '/', component: { template: '<div />' } },
    ],
  })
  await router.push('/o/acme/p/PROJ/items/PROJ-1')
  await router.isReady()

  const organizations = useOrganizationsStore()
  organizations.organizations = [
    { id: 'org-1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory: false },
  ]
  organizations.select('acme')
  useSessionStore().set({
    id: 'u1',
    email: 'u1@acme.dev',
    firstName: 'U',
    lastName: 'One',
    fullName: 'U One',
    roles: [],
    isAgent: false,
    avatarKey: null,
    timeZone: null,
  })

  const wrapper = mount(ItemDetail, {
    props: { slug: 'acme', projectKey: 'PROJ', itemKey },
    global: {
      plugins: [
        pinia,
        router,
        // As in main.ts: a 404 is final, not retried.
        [VueQueryPlugin, { queryClientConfig: { defaultOptions: { queries: { retry: false } } } }],
      ],
      stubs: { RouterLink: RouterLinkStub, Teleport: true },
    },
  })
  await flushPromises()
  return wrapper
}

const disabled = (wrapper: Awaited<ReturnType<typeof mountDetail>>, testId: string) =>
  wrapper
    .findAll(`[data-testid="${testId}"]`)
    .map((button) => button.attributes('disabled') !== undefined)

beforeEach(() => {
  patch = () =>
    new Response(JSON.stringify({ ...item, title: 'Crash on save, again', version: 5 }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })
  toast.success.mockClear()
  toast.error.mockClear()
  toast.info.mockClear()
})

afterEach(() => {
  vi.unstubAllGlobals()
  setActivePinia(createPinia())
})

describe('saving an item', () => {
  it('offers Save and Save & close only once something has changed', async () => {
    const wrapper = await mountDetail()

    // Both copies of the row - the phone strip and the rail - are rendered at once.
    expect(disabled(wrapper, 'item-save')).toEqual([true, true])
    expect(disabled(wrapper, 'item-save-close')).toEqual([true, true])

    await wrapper.find('#item-title').setValue('Crash on save, again')

    expect(disabled(wrapper, 'item-save')).toEqual([false, false])
    expect(disabled(wrapper, 'item-save-close')).toEqual([false, false])
  })

  it('says so, briefly, when the save goes through', async () => {
    const wrapper = await mountDetail()
    await wrapper.find('#item-title').setValue('Crash on save, again')

    await wrapper.find('[data-testid="item-save"]').trigger('click')
    // The save reports only once the lists it invalidated have come back.
    await vi.waitFor(() => expect(toast.success).toHaveBeenCalled())

    expect(toast.success).toHaveBeenCalledWith(
      'PROJ-1 saved.',
      expect.objectContaining({ duration: 2000 }),
    )
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('says what went wrong when the save is refused', async () => {
    const wrapper = await mountDetail()
    await wrapper.find('#item-title').setValue('Crash on save, again')
    patch = () =>
      new Response(JSON.stringify({ title: 'Someone changed this item first.', status: 409 }), {
        status: 409,
        headers: { 'content-type': 'application/problem+json' },
      })

    await wrapper.find('[data-testid="item-save"]').trigger('click')
    await flushPromises()

    expect(toast.error).toHaveBeenCalledWith('Someone changed this item first.', expect.anything())
    expect(toast.success).not.toHaveBeenCalled()
    // The conflict keeps its standing line as well: it is the one that says what to do next.
    expect(wrapper.text()).toContain('Reload to compare before overwriting')
  })
})

describe('the save toast helpers', () => {
  it('lets a save confirmation go quickly and a failure stay', () => {
    const { saved, saveFailed } = useToast()

    saved('Capacity saved.')
    saveFailed(new Error('boom'), 'Capacity could not be saved.')

    expect(toast.success).toHaveBeenCalledWith(
      'Capacity saved.',
      expect.objectContaining({ duration: 2000 }),
    )
    expect(toast.error).toHaveBeenCalledWith('Capacity could not be saved.')
  })
})

describe('copying from the item', () => {
  let writeText: ReturnType<typeof vi.fn>
  beforeEach(() => {
    writeText = vi.fn(async () => {})
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
  })

  it('copies a shareable link to the item and says so', async () => {
    const wrapper = await mountDetail()
    await wrapper.find('button[aria-label="Copy link"]').trigger('click')
    await flushPromises()

    expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/o/acme/p/PROJ/items/PROJ-1`)
    expect(toast.success).toHaveBeenCalledWith('Link copied', expect.anything())
  })

  it('explains each copy button on hover', async () => {
    const wrapper = await mountDetail()

    expect(wrapper.find('button[aria-label="Copy link"]').attributes('title')).toBe(
      'Copy a link to this item',
    )
    expect(wrapper.find('button[aria-label="Copy branch name"]').attributes('title')).toBe(
      'Copy a git branch name for this item',
    )
    expect(wrapper.find('button[aria-label="Copy commit prefix"]').attributes('title')).toBe(
      'Copy a commit message prefix (PROJ-1: )',
    )
  })

  it('reports a clipboard the browser refused', async () => {
    writeText.mockRejectedValueOnce(new Error('denied'))
    const wrapper = await mountDetail()
    await wrapper.find('button[aria-label="Copy link"]').trigger('click')
    await flushPromises()

    expect(toast.error).toHaveBeenCalledWith('Could not copy to the clipboard.')
    expect(toast.success).not.toHaveBeenCalled()
  })
})

describe('following a link to an item', () => {
  it('says the item cannot be opened when the API hides it, instead of loading forever', async () => {
    const wrapper = await mountDetail('PROJ-404')

    expect(wrapper.text()).toContain('PROJ-404 could not be opened.')
    expect(wrapper.text()).toContain('It does not exist, or you do not have access to it.')
    expect(wrapper.text()).not.toContain('Loading item')
  })
})
