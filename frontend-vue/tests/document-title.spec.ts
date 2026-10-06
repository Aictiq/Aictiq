/* eslint-disable vue/one-component-per-file -- stand-ins for an app root and a page, not components of their own */
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, h, nextTick, ref } from 'vue'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { Project } from '@/api/projects'
import { formatDocumentTitle, useDocumentTitle, usePageTitle } from '@/composables/useDocumentTitle'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'

const project = (key: string, name: string): Project => ({
  id: key,
  key,
  name,
  description: null,
  visibility: 'organization',
  icon: null,
  color: null,
  isArchived: false,
  createdAt: '2026-10-01T00:00:00Z',
  role: 'admin',
  version: 1,
})

const Page = { template: '<div />' }
const claimed = ref<string | null>(null)
const Claiming = defineComponent({
  setup() {
    usePageTitle(claimed)
    return () => h('div')
  },
})

let wrapper: ReturnType<typeof mount> | undefined

beforeEach(() => {
  const entries = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => entries.get(key) ?? null,
    setItem: (key: string, value: string) => void entries.set(key, value),
    removeItem: (key: string) => void entries.delete(key),
  })
  setActivePinia(createPinia())
  const organizations = useOrganizationsStore()
  organizations.organizations = [
    { id: 'o1', slug: 'acme', name: 'Acme Corp', role: 'owner' } as never,
  ]
  organizations.currentSlug = 'acme'
  const projects = useProjectsStore()
  projects.projects = [project('IDA', 'Ida app'), project('HLQ', 'HairLinQ')]
  projects.select('HLQ')
  claimed.value = null
  document.title = 'Aictiq'
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  vi.unstubAllGlobals()
})

async function render(path: string) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: Page },
      { path: '/login', name: 'login', component: Page, meta: { requiresAuth: false, title: 'Sign in' } },
      { path: '/board', name: 'board', component: Page, meta: { requiresAuth: true, title: 'Board' } },
      { path: '/o/:slug/p/:projectKey/board', component: Page, meta: { requiresAuth: true, title: 'Board' } },
      { path: '/o/:slug/p/:projectKey/items/:itemKey', component: Claiming, meta: { requiresAuth: true, title: 'Item' } },
      { path: '/o/:slug/factory/runs', component: Page, meta: { requiresAuth: true, title: 'Runs' } },
    ],
  })
  await router.push(path)
  const Root = defineComponent({
    setup() {
      useDocumentTitle()
      return () => h(RouterView)
    },
  })
  wrapper = mount(Root, { global: { plugins: [router] } })
  await nextTick()
  return router
}

describe('formatDocumentTitle', () => {
  it('puts the page first, drops empty and repeated parts, and ends with the app', () => {
    expect(formatDocumentTitle(['Board', 'HairLinQ'])).toBe('Board · HairLinQ · Aictiq')
    expect(formatDocumentTitle(['Board', null, undefined, ''])).toBe('Board · Aictiq')
    expect(formatDocumentTitle(['Board', 'Aictiq'])).toBe('Board · Aictiq')
    expect(formatDocumentTitle([])).toBe('Aictiq')
  })
})

describe('useDocumentTitle', () => {
  it('names the page and the project from the URL', async () => {
    await render('/o/acme/p/IDA/board')
    expect(document.title).toBe('Board · Ida app · Aictiq')
  })

  it('follows navigation', async () => {
    const router = await render('/o/acme/p/IDA/board')
    await router.push('/o/acme/factory/runs')
    expect(document.title).toBe('Runs · Acme Corp · Aictiq')
    await router.push('/')
    expect(document.title).toBe('My work · Aictiq')
    await router.push('/login')
    expect(document.title).toBe('Sign in · Aictiq')
  })

  it('reads the unscoped board project from the store', async () => {
    await render('/board')
    expect(document.title).toBe('Board · HairLinQ · Aictiq')
  })

  it('never names another organization or its project', async () => {
    await render('/o/other/p/IDA/board')
    expect(document.title).toBe('Board · IDA · Aictiq')
    await render('/o/other/factory/runs')
    expect(document.title).toBe('Runs · other · Aictiq')
  })

  it('uses the item key until the item claims the tab, and hands it back on leave', async () => {
    const router = await render('/o/acme/p/IDA/items/IDA-4')
    expect(document.title).toBe('IDA-4 · Aictiq')
    claimed.value = 'IDA-4 Fix the login'
    await nextTick()
    expect(document.title).toBe('IDA-4 Fix the login · Aictiq')
    await router.push('/o/acme/p/IDA/board')
    expect(document.title).toBe('Board · Ida app · Aictiq')
  })
})
