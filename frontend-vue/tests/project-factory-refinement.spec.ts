import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'

import { getFactorySettings, updateFactorySettings, type FactorySettings } from '@/api/playbooks'
import { listAgents, type Agent } from '@/api/agents'
import { listProjectMembers, type ProjectMember } from '@/api/projects'
import { listRunnerChoices, type RunnerChoice } from '@/api/runners'
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
vi.mock('@/api/runners', () => ({ listRunnerChoices: vi.fn(async () => []) }))

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
  vi.mocked(listAgents).mockResolvedValue([])
  vi.mocked(listProjectMembers).mockResolvedValue([])
  vi.mocked(listRunnerChoices).mockResolvedValue([])
})

describe('default runner', () => {
  const runner = (id: string, name: string, isOnline = true) =>
    ({ id, name, harnesses: ['claude'], isOnline }) as RunnerChoice

  it('offers any free runner first, then the organization runners, and saves the choice', async () => {
    vi.mocked(listRunnerChoices).mockResolvedValue([
      runner('r1', 'hetzner-vm'),
      runner('r2', 'laptop', false),
    ])
    vi.mocked(updateFactorySettings).mockImplementation(async (_slug, _key, body) => ({
      ...settings,
      ...body,
      version: 2,
    }))
    const { wrapper } = await mountFactory('')

    const select = wrapper.get('#factory-default-runner')
    expect(select.findAll('option').map((option) => option.text())).toEqual([
      'Any free runner',
      'hetzner-vm',
      'laptop (offline)',
    ])
    expect((select.element as HTMLSelectElement).value).toBe('')
    expect(wrapper.text()).toContain(
      'Project chat and new runs use this runner when none is chosen.',
    )

    await select.setValue('r2')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(vi.mocked(updateFactorySettings).mock.calls[0]![2]).toMatchObject({
      defaultRunnerId: 'r2',
    })
    expect((select.element as HTMLSelectElement).value).toBe('r2')

    await select.setValue('')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(vi.mocked(updateFactorySettings).mock.calls[1]![2]).toMatchObject({
      defaultRunnerId: null,
    })
  })

  it('keeps a saved runner the roster does not list', async () => {
    vi.mocked(getFactorySettings).mockResolvedValue({ ...settings, defaultRunnerId: 'r9' })
    const { wrapper } = await mountFactory('')

    const select = wrapper.get('#factory-default-runner')
    expect((select.element as HTMLSelectElement).value).toBe('r9')
    expect(select.findAll('option').map((option) => option.text())).toEqual([
      'Any free runner',
      'Saved runner',
    ])
  })
})

describe('automatic agent hint', () => {
  function roster(
    rows: {
      userId: string
      displayName: string
      isActive?: boolean
      isDefault?: boolean
      accessible?: boolean
    }[],
  ) {
    vi.mocked(listAgents).mockResolvedValue(
      rows.map((row) => ({
        ...row,
        isActive: row.isActive ?? true,
        isDefault: row.isDefault ?? false,
      })) as Agent[],
    )
    vi.mocked(listProjectMembers).mockResolvedValue(
      rows
        .filter((row) => row.accessible !== false)
        .map((row) => ({ userId: row.userId, isAgent: true })) as ProjectMember[],
    )
  }

  it('names the eligible organization default ahead of other eligible agents', async () => {
    roster([
      { userId: 'a1', displayName: 'Alpha' },
      { userId: 'a2', displayName: 'Beta', isDefault: true },
    ])
    const { wrapper } = await mountFactory('')

    expect(wrapper.get('[data-testid="automatic-agent-hint"]').text()).toBe(
      'Runs use Beta, the organization default, when no agent is chosen.',
    )
    expect((wrapper.get('#factory-default-agent').element as HTMLSelectElement).value).toBe('')
  })

  it.each([{ isActive: false }, { accessible: false }])(
    'skips an ineligible organization default and names the sole eligible fallback %o',
    async (ineligible) => {
      roster([
        { userId: 'a1', displayName: 'Alpha' },
        { userId: 'a2', displayName: 'Beta', isDefault: true, ...ineligible },
      ])
      const { wrapper } = await mountFactory('')

      expect(wrapper.get('[data-testid="automatic-agent-hint"]').text()).toBe(
        'Runs use Alpha, the only eligible agent, when no agent is chosen.',
      )
    },
  )

  it('asks for a choice when several agents have no eligible organization default', async () => {
    roster([
      { userId: 'a1', displayName: 'Alpha' },
      { userId: 'a2', displayName: 'Beta' },
    ])
    const { wrapper } = await mountFactory('')

    expect(wrapper.get('[data-testid="automatic-agent-hint"]').text()).toContain(
      'Choose an agent when starting a run',
    )
    await wrapper.get('#factory-default-agent').setValue('a1')
    expect(wrapper.find('[data-testid="automatic-agent-hint"]').exists()).toBe(false)
  })
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
