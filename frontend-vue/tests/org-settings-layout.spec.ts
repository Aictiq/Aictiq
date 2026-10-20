import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { describe, expect, it, vi, afterEach } from 'vitest'

import OrgSettingsLayout from '@/views/settings/OrgSettingsLayout.vue'

/**
 * Organization settings - its agents, its members, the rest - are the operators' side of an
 * organization, as the factory is. A stakeholder or Guest who pastes a link to one gets a
 * refusal and none of the tabs; Owners and Admins always operate, so they are never refused.
 */
const org = {
  id: 'org-1',
  slug: 'acme',
  name: 'Acme',
  role: 'member',
  canOperateFactory: false,
  plan: 'trial',
  timeZone: 'UTC',
  weekStart: 'monday',
  membersCanCreateProjects: true,
  createdAt: '2026-01-01T00:00:00Z',
  version: 1,
}

function stubFetch(role: string, canOperateFactory: boolean) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown) => {
      const body = /\/orgs\/acme$/.test(String(input).split('?')[0] ?? '')
        ? { ...org, role, canOperateFactory }
        : {}
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
}

async function mountLayout(tab: string) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      {
        path: '/o/:slug/settings/:tab',
        component: { template: '<div data-testid="settings-child">tab</div>' },
      },
      { path: '/', component: { template: '<div />' } },
    ],
  })
  await router.push(`/o/acme/settings/${tab}`)
  await router.isReady()

  const wrapper = mount(OrgSettingsLayout, {
    global: { plugins: [createPinia(), router, VueQueryPlugin] },
  })
  await flushPromises()
  return wrapper
}

afterEach(() => vi.unstubAllGlobals())

describe('OrgSettingsLayout for someone who may not operate the factory', () => {
  it.each([
    ['member', 'agents'],
    ['member', 'members'],
    ['guest', 'general'],
  ])('refuses a %s on the %s tab, with no tabs or child content', async (role, tab) => {
    stubFetch(role, false)
    const wrapper = await mountLayout(tab)

    expect(wrapper.text()).toContain('Organization settings are not available to you')
    expect(wrapper.find('[data-testid="settings-child"]').exists()).toBe(false)
    expect(wrapper.findAll('a').some((a) => a.attributes('href')?.includes('/settings/'))).toBe(
      false,
    )
  })

  it('renders the tab for an operator', async () => {
    stubFetch('member', true)
    const wrapper = await mountLayout('agents')

    expect(wrapper.text()).not.toContain('Organization settings are not available to you')
    expect(wrapper.find('[data-testid="settings-child"]').exists()).toBe(true)
    expect(
      wrapper.findAll('a').some((a) => a.attributes('href') === '/o/acme/settings/agents'),
    ).toBe(true)
  })
})
