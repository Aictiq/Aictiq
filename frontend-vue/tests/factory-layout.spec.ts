import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { describe, expect, it, vi, afterEach } from 'vitest'

import FactoryLayout from '@/views/factory/FactoryLayout.vue'

/**
 * The factory sits behind one gate and this layout is where it lives. A
 * stakeholder who pastes a run link gets the same refusal as one who clicks the
 * (absent) sidebar entry - and nothing under the route renders for them.
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

function stubFetch(canOperateFactory: boolean) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(JSON.stringify({ ...org, canOperateFactory }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    ),
  )
}

async function mountLayout() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      {
        path: '/o/:slug/factory/:pathMatch(.*)?',
        component: { template: '<div data-testid="factory-child">runs</div>' },
      },
      { path: '/', component: { template: '<div />' } },
    ],
  })
  await router.push('/o/acme/factory/runs/r-1')
  await router.isReady()

  const wrapper = mount(FactoryLayout, {
    global: {
      plugins: [createPinia(), router, VueQueryPlugin],
      // The gate lives in this layout, not in the app frame it sits inside.
      stubs: { AppShell: { template: '<div><slot /></div>' } },
    },
  })
  await flushPromises()
  return wrapper
}

afterEach(() => vi.unstubAllGlobals())

describe('FactoryLayout as the operator gate', () => {
  it('refuses a stakeholder who pastes a factory link, with no child content', async () => {
    stubFetch(false)
    const wrapper = await mountLayout()

    expect(wrapper.text()).toContain('The factory is not available to you')
    expect(wrapper.find('[data-testid="factory-child"]').exists()).toBe(false)
  })

  it('renders the requested tab for an operator', async () => {
    stubFetch(true)
    const wrapper = await mountLayout()

    expect(wrapper.text()).not.toContain('The factory is not available to you')
    expect(wrapper.find('[data-testid="factory-child"]').exists()).toBe(true)
  })
})
