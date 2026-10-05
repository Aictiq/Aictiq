// @vitest-environment jsdom
import { mount, flushPromises } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

import Markdown from '@/components/common/Markdown.vue'
import { getItem, type WorkItem } from '@/api/items'

vi.mock('@/api/items', () => ({ getItem: vi.fn() }))
const lookup = vi.mocked(getItem)
const wrappers: ReturnType<typeof mount>[] = []
afterEach(() => { wrappers.forEach((wrapper) => wrapper.unmount()); wrappers.length = 0; vi.resetAllMocks() })

async function render(source: string, withContext = true) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }] })
  await router.push('/')
  const wrapper = mount(Markdown, {
    props: { source, ...(withContext ? { slug: 'acme', projectKey: 'ACME' } : {}) },
    global: { plugins: [[VueQueryPlugin, { queryClient: client }], router] },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, router }
}

describe('rendered ticket resolution', () => {
  it('looks up only unique visible project references and leaves unknown keys plain', async () => {
    lookup.mockImplementation(async (_slug, key) => {
      if (key === 'ACME-999') throw new Error('Not found')
      return { key } as WorkItem
    })
    const { wrapper, router } = await render('See #ACME-12 (#ACME-12) #ACME-999 #OTHER-1 `#ACME-2`')
    expect(lookup.mock.calls).toEqual([['acme', 'ACME-12'], ['acme', 'ACME-999']])
    expect(wrapper.findAll('a')).toHaveLength(2)
    expect(wrapper.text()).toContain('#ACME-999 #OTHER-1 #ACME-2')
    await wrapper.find('a').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/o/acme/p/ACME/items/ACME-12')
  })

  it('does not resolve references without project context', async () => {
    const { wrapper } = await render('#ACME-12', false)
    expect(lookup).not.toHaveBeenCalled()
    expect(wrapper.findAll('a')).toHaveLength(0)
  })

  it('drops old links when the project changes and preserves modified clicks', async () => {
    lookup.mockImplementation(async (_slug, key) => ({ key }) as WorkItem)
    const { wrapper, router } = await render('#ACME-12 #WEB-1')
    await wrapper.find('a').trigger('click', { ctrlKey: true })
    expect(router.currentRoute.value.path).toBe('/')
    await wrapper.setProps({ projectKey: 'WEB' })
    await flushPromises()
    expect(wrapper.findAll('a').map((link) => link.text())).toEqual(['#WEB-1'])
    expect(wrapper.find('a').attributes('href')).toBe('/o/acme/p/WEB/items/WEB-1')
  })
})
