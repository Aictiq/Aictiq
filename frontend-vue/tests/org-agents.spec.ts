import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'

import { disableAgent, listAgents, updateAgent, type Agent } from '@/api/agents'
import { orgScopeKey } from '@/composables/useSettingsScope'
import OrgAgentsView from '@/views/settings/OrgAgentsView.vue'

vi.mock('@/api/agents', () => ({
  listAgents: vi.fn(),
  updateAgent: vi.fn(),
  disableAgent: vi.fn(),
}))
vi.mock('@/api/agent-activity', () => ({
  getAgentContribution: vi.fn(async () => null),
  listAgentActivity: vi.fn(async () => []),
}))
vi.mock('@/composables/useToast', () => ({
  useToast: () => ({ saved: vi.fn(), success: vi.fn(), error: vi.fn() }),
}))

let agents: Agent[]
const wrappers: ReturnType<typeof mount>[] = []
const passthrough = { template: '<div><slot /></div>' }

beforeEach(() => {
  vi.clearAllMocks()
  agents = [
    { userId: 'a1', displayName: 'Alpha', isDefault: true, isActive: true },
    { userId: 'a2', displayName: 'Beta', isDefault: false, isActive: true },
    { userId: 'a3', displayName: 'Retired', isDefault: false, isActive: false },
  ].map((agent) => ({
    ...agent,
    email: `${agent.userId}@agents.invalid`,
    ownerUserId: 'u1',
    ownerName: 'Owner',
    role: 'member',
    createdAt: '2026-01-01T00:00:00Z',
    lastActiveAt: null,
    tokenCount: 0,
  }))
  vi.mocked(listAgents).mockImplementation(async () => agents.map((agent) => ({ ...agent })))
  vi.mocked(updateAgent).mockImplementation(async (_slug, id, body) => {
    agents = agents.map((agent) => ({
      ...agent,
      isDefault: body.isDefault
        ? agent.userId === id
        : agent.userId === id
          ? false
          : agent.isDefault,
    }))
    return agents.find((agent) => agent.userId === id)!
  })
  vi.mocked(disableAgent).mockImplementation(async (_slug, id) => {
    agents = agents.map((agent) =>
      agent.userId === id ? { ...agent, isActive: false, isDefault: false } : agent,
    )
  })
})

afterEach(() => wrappers.splice(0).forEach((wrapper) => wrapper.unmount()))

async function mountAgents(role = 'admin') {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: passthrough }],
  })
  await router.push('/')
  const wrapper = mount(OrgAgentsView, {
    global: {
      plugins: [createPinia(), router],
      provide: {
        [orgScopeKey as symbol]: { slug: ref('acme'), record: ref({ role }) },
      },
      stubs: {
        AgentConnectSheet: true,
        DropdownMenu: passthrough,
        DropdownMenuTrigger: passthrough,
        DropdownMenuContent: passthrough,
        DropdownMenuItem: {
          props: ['disabled'],
          emits: ['select'],
          template: '<button :disabled="disabled" @click="$emit(\'select\')"><slot /></button>',
        },
      },
    },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return wrapper
}

describe('organization default agent', () => {
  it('moves the Default badge after an admin selects another agent', async () => {
    const wrapper = await mountAgents()
    expect(wrapper.findAll('ul > li')[0]!.text()).toContain('Default')

    const beta = wrapper.findAll('ul > li')[1]!
    await beta
      .findAll('button')
      .find((button) => button.text() === 'Set as organization default')!
      .trigger('click')
    await flushPromises()

    expect(updateAgent).toHaveBeenCalledWith('acme', 'a2', { isDefault: true })
    expect(listAgents).toHaveBeenCalledTimes(2)
    expect(wrapper.findAll('ul > li')[0]!.text()).not.toContain('Clear organization default')
    expect(wrapper.findAll('ul > li')[1]!.text()).toContain('Default')
    expect(wrapper.findAll('ul > li')[1]!.text()).toContain('Clear organization default')
  })

  it('lets an admin clear the default', async () => {
    const wrapper = await mountAgents()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Clear organization default')!
      .trigger('click')
    await flushPromises()

    expect(updateAgent).toHaveBeenCalledWith('acme', 'a1', { isDefault: false })
    expect(wrapper.text()).not.toContain('Clear organization default')
    expect(wrapper.findAll('span').filter((span) => span.text() === 'Default')).toHaveLength(0)
  })

  it('keeps the current badge when updating the default fails', async () => {
    vi.mocked(updateAgent).mockRejectedValueOnce(new Error('Unavailable'))
    const wrapper = await mountAgents()
    await wrapper
      .findAll('ul > li')[1]!
      .findAll('button')
      .find((button) => button.text() === 'Set as organization default')!
      .trigger('click')
    await flushPromises()

    expect(wrapper.findAll('ul > li')[0]!.text()).toContain('Clear organization default')
    expect(wrapper.findAll('ul > li')[1]!.text()).not.toContain('Clear organization default')
    expect(listAgents).toHaveBeenCalledTimes(1)
  })

  it('prevents a disabled agent from being selected as default', async () => {
    const wrapper = await mountAgents()
    const retired = wrapper.findAll('ul > li')[2]!
    const setDefault = retired
      .findAll('button')
      .find((button) => button.text() === 'Set as organization default')!

    expect((setDefault.element as HTMLButtonElement).disabled).toBe(true)
    await setDefault.trigger('click')
    expect(updateAgent).not.toHaveBeenCalled()
  })

  it('refreshes the badge when disabling the current default', async () => {
    const wrapper = await mountAgents()
    await wrapper
      .findAll('ul > li')[0]!
      .findAll('button')
      .find((button) => button.text() === 'Disable and revoke its tokens')!
      .trigger('click')
    await flushPromises()

    expect(disableAgent).toHaveBeenCalledWith('acme', 'a1')
    expect(wrapper.findAll('span').filter((span) => span.text() === 'Default')).toHaveLength(0)
  })

  it('shows the default to members without exposing management actions', async () => {
    const wrapper = await mountAgents('member')

    expect(wrapper.findAll('span').some((span) => span.text() === 'Default')).toBe(true)
    expect(
      wrapper.findAll('button').some((button) => button.text().includes('organization default')),
    ).toBe(false)
    expect(wrapper.find('[aria-label="Manage Alpha"]').exists()).toBe(false)
  })
})
