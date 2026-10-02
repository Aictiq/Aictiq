import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'

import { getFactorySettings, type FactorySettings } from '@/api/playbooks'
import RefinementSettingsForm from '@/components/settings/RefinementSettingsForm.vue'
import { projectScopeKey } from '@/composables/useSettingsScope'
import { useOrganizationsStore } from '@/stores/organizations'
import ProjectFactoryView from '@/views/settings/ProjectFactoryView.vue'

vi.mock('@/api/playbooks', () => ({
  getFactorySettings: vi.fn(),
  updateFactorySettings: vi.fn(),
}))
vi.mock('@/api/github', () => ({ listGitHubBindings: vi.fn(async () => []) }))
vi.mock('@/api/projects', () => ({ listProjectMembers: vi.fn(async () => []) }))
vi.mock('@/api/agents', () => ({ listAgents: vi.fn(async () => []) }))

const settings: FactorySettings = {
  projectId: 'p1',
  repoSource: 1,
  repoFullName: null,
  defaultBranch: 'main',
  localPathHint: null,
  defaultAgentId: null,
  updatedAt: null,
  version: 1,
}
const path = '/o/acme/p/WEB/settings/factory'
const wrappers: ReturnType<typeof mount>[] = []

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getFactorySettings).mockResolvedValue(settings)
})

afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  vi.restoreAllMocks()
})

async function mountFactory(hash = '#ticket-refinement', canOperateFactory = true, role = 'admin') {
  const pinia = createPinia()
  useOrganizationsStore(pinia).organizations = [
    { id: 'org-1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory },
  ]
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path, component: { template: '<div />' } }],
  })
  await router.push(`${path}${hash}`)
  const scrollIntoView = vi.spyOn(HTMLElement.prototype, 'scrollIntoView')
  const wrapper = mount(ProjectFactoryView, {
    global: {
      plugins: [pinia, router],
      provide: {
        [projectScopeKey as symbol]: {
          slug: ref('acme'),
          projectKey: ref('WEB'),
          record: ref({ role, isArchived: false }),
        },
      },
      stubs: { RefinementSettingsForm: true },
    },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, router, scrollIntoView }
}

describe('project refinement settings deep link', () => {
  it('waits for the refinement form to load before scrolling a direct hash link', async () => {
    let resolveSettings!: (value: FactorySettings) => void
    vi.mocked(getFactorySettings).mockReturnValue(
      new Promise((resolve) => {
        resolveSettings = resolve
      }),
    )
    const { wrapper, scrollIntoView } = await mountFactory()
    expect(wrapper.find('#ticket-refinement').exists()).toBe(false)
    expect(scrollIntoView).not.toHaveBeenCalled()

    resolveSettings(settings)
    await flushPromises()

    expect(wrapper.get('#ticket-refinement').text()).toContain('Ticket refinement')
    expect(scrollIntoView).not.toHaveBeenCalled()

    wrapper.getComponent(RefinementSettingsForm).vm.$emit('loaded')
    expect(scrollIntoView).not.toHaveBeenCalled()
    await flushPromises()

    expect(scrollIntoView).toHaveBeenCalledExactlyOnceWith({ block: 'start' })
    expect(scrollIntoView.mock.contexts[0]).toBe(wrapper.get('#ticket-refinement').element)
  })

  it('scrolls when the hash changes on the loaded factory page', async () => {
    const { wrapper, router, scrollIntoView } = await mountFactory('')
    expect(wrapper.find('#ticket-refinement').exists()).toBe(true)
    wrapper.getComponent(RefinementSettingsForm).vm.$emit('loaded')
    await flushPromises()
    expect(scrollIntoView).not.toHaveBeenCalled()

    await router.push(`${path}#ticket-refinement`)
    await flushPromises()

    expect(scrollIntoView).toHaveBeenCalledExactlyOnceWith({ block: 'start' })
    expect(scrollIntoView.mock.contexts[0]).toBe(wrapper.get('#ticket-refinement').element)
  })

  it('does not scroll when factory settings fail to load', async () => {
    vi.mocked(getFactorySettings).mockRejectedValue(new Error('Unavailable'))
    const { wrapper, scrollIntoView } = await mountFactory()
    expect(wrapper.text()).toContain('Could not load factory settings')
    expect(wrapper.find('#ticket-refinement').exists()).toBe(false)
    expect(scrollIntoView).not.toHaveBeenCalled()
  })

  it.each([
    [false, 'admin'],
    [true, 'guest'],
  ])('does not scroll for factory permission %s and project role %s', async (canOperate, role) => {
    const { wrapper, scrollIntoView } = await mountFactory('#ticket-refinement', canOperate, role)
    expect(wrapper.find('#ticket-refinement').exists()).toBe(false)
    expect(scrollIntoView).not.toHaveBeenCalled()
    if (role === 'guest') expect(getFactorySettings).not.toHaveBeenCalled()
  })
})
