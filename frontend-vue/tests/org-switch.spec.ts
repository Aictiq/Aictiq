/* eslint-disable vue/one-component-per-file -- stand-ins for one menu, not components of their own */
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'

import CreateOrganizationDialog from '@/components/shell/CreateOrganizationDialog.vue'
import OrgSwitcher from '@/components/shell/OrgSwitcher.vue'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * Switching organizations when you are not the same person in both.
 *
 * A URL under `/o/{slug}` is the authority the router guard trusts, so a switch that left
 * the app on another organization's page would be undone by the next navigation — and in
 * between, the sidebar, the role chip and the factory entry would describe one
 * organization while the page beside them belonged to the other. That is only ever
 * noticed by someone whose standing differs between the two, which is exactly the person
 * who cannot afford to be confused about it.
 */

/**
 * The real menu is Reka's, which teleports and only renders its items once opened. The
 * items themselves are what this is about, so they are rendered inline and `@select` is
 * driven by a click.
 */
vi.mock('@/components/ui/dropdown-menu', () => {
  const passthrough = (tag: string) =>
    defineComponent({ setup: (_, { slots }) => () => h(tag, slots.default?.()) })
  return {
    DropdownMenu: passthrough('div'),
    DropdownMenuTrigger: passthrough('button'),
    DropdownMenuContent: passthrough('div'),
    DropdownMenuLabel: passthrough('div'),
    DropdownMenuSeparator: defineComponent({ setup: () => () => h('hr') }),
    DropdownMenuItem: defineComponent({
      emits: ['select'],
      setup: (_, { slots, emit }) => () =>
        h('button', { class: 'menu-item', onClick: () => emit('select') }, slots.default?.()),
    }),
  }
})
const acme = { id: 'o1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory: false } as const
const dana = { id: 'o2', slug: 'dana-co', name: 'Dana & Co', role: 'owner', canOperateFactory: true } as const

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div>my work</div>' } },
      {
        path: '/o/:slug/p/:projectKey/items',
        name: 'project-items',
        component: { template: '<div>items</div>' },
      },
      { path: '/inbox', name: 'inbox', component: { template: '<div>inbox</div>' } },
    ],
  })
}

async function mountSwitcher(at: string) {
  const router = makeRouter()
  await router.push(at)
  await router.isReady()

  const organizations = useOrganizationsStore()
  organizations.organizations = [{ ...acme }, { ...dana }]
  organizations.status = 'ready'
  organizations.select('acme')

  const wrapper = mount(OrgSwitcher, { global: { plugins: [router] } })
  return { wrapper, router, organizations }
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.stubGlobal('localStorage', {
    getItem: () => null,
    setItem: () => {},
    removeItem: () => {},
    clear: () => {},
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('the organization switcher', () => {
  it('leaves a page that belongs to the organization being left', async () => {
    const { wrapper, router, organizations } = await mountSwitcher('/o/acme/p/WEB/items')

    await wrapper.find('.menu-item').trigger('click')
    await flushPromises()

    expect(organizations.currentSlug).toBe('dana-co')
    // Not /o/acme/… any more: the page they were on is not theirs to be on now.
    expect(router.currentRoute.value.path).toBe('/')
  })

  it('stays put when the page belongs to no organization in particular', async () => {
    const { wrapper, router, organizations } = await mountSwitcher('/inbox')

    await wrapper.find('.menu-item').trigger('click')
    await flushPromises()

    expect(organizations.currentSlug).toBe('dana-co')
    // The inbox is personal and spans organizations; switching should not eject them.
    expect(router.currentRoute.value.path).toBe('/inbox')
  })

  it('shows the role of the organization it is pointing at', async () => {
    const { wrapper, organizations } = await mountSwitcher('/inbox')

    expect(wrapper.text()).toContain('member')

    organizations.select('dana-co')
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain('owner')
  })

  it('leaves the old organization behind when a new one is created', async () => {
    const { wrapper, router, organizations } = await mountSwitcher('/o/acme/p/WEB/items')
    // The store selects what it just created; the page has to follow, or someone lands in
    // a brand-new organization looking at the previous one's project.
    organizations.organizations = [
      ...organizations.organizations,
      { id: 'o3', slug: 'newco', name: 'New Co', role: 'owner', canOperateFactory: true },
    ]

    wrapper.findComponent(CreateOrganizationDialog).vm.$emit('created', 'newco')
    await flushPromises()

    expect(organizations.currentSlug).toBe('newco')
    expect(router.currentRoute.value.path).toBe('/')
  })
})
