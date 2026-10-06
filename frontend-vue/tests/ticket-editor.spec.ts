import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { EditorContent, type Editor } from '@tiptap/vue-3'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'

import { getItem, type WorkItem } from '@/api/items'
import { searchProject, type SearchResponse } from '@/api/search'
import MarkdownEditor from '@/components/common/MarkdownEditor.vue'

vi.mock('@/api/search', () => ({ searchProject: vi.fn() }))
vi.mock('@/api/items', () => ({ getItem: vi.fn() }))
vi.mock('@/composables/useToast', () => ({ useToast: () => ({ error: vi.fn() }) }))

const tickets: SearchResponse = {
  items: [
    { id: '1', key: 'ACME-12', title: 'Fix login', snippet: '', rank: 1 },
    { id: '2', key: 'ACME-24', title: 'Login button', snippet: '', rank: 1 },
  ],
  comments: [],
  pages: [],
}
let wrapper: VueWrapper
let editor: Editor

async function mountEditor(props: Partial<InstanceType<typeof MarkdownEditor>['$props']> = {}) {
  wrapper = mount(MarkdownEditor, {
    attachTo: document.body,
    props: { modelValue: '', slug: 'acme', projectKey: 'ACME', ...props },
  })
  await nextTick()
  editor = wrapper.findComponent(EditorContent).props('editor') as Editor
  // Layout is the browser's responsibility; suggestions still use the real Tiptap document,
  // transactions, focus, selection and key handlers in these component integration tests.
  vi.spyOn(editor.view, 'coordsAtPos').mockReturnValue({ left: 0, right: 0, top: 0, bottom: 10 })
  await vi.advanceTimersByTimeAsync(0)
  editor.view.dom.focus()
  expect(editor.isFocused).toBe(true)
  await nextTick()
}

async function typeText(text: string) {
  editor.commands.insertContent({ type: 'text', text })
  await nextTick()
}

async function searchReady() {
  await vi.advanceTimersByTimeAsync(150)
  await flushPromises()
}

function deferred() {
  let resolve!: (value: SearchResponse) => void
  const promise = new Promise<SearchResponse>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

beforeEach(() => {
  vi.useFakeTimers()
  vi.mocked(searchProject).mockReset().mockResolvedValue(tickets)
})

afterEach(() => {
  wrapper?.unmount()
  vi.useRealTimers()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('ticket suggestions in the Markdown editor', () => {
  it('fetches only after a trigger, debounces typing and scopes results to this project', async () => {
    vi.mocked(searchProject).mockResolvedValue({
      ...tickets,
      items: [
        ...tickets.items,
        { id: '3', key: 'OTHER-1', title: 'Private ticket', snippet: '', rank: 1 },
      ],
    })
    await mountEditor()
    await vi.advanceTimersByTimeAsync(500)
    expect(searchProject).not.toHaveBeenCalled()

    await typeText('#log')
    expect(wrapper.get('[data-testid="ticket-list"]').text()).toContain('Loading tickets')
    await vi.advanceTimersByTimeAsync(100)
    await typeText('in')
    await vi.advanceTimersByTimeAsync(149)
    expect(searchProject).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    await flushPromises()

    expect(searchProject).toHaveBeenCalledExactlyOnceWith('acme', 'ACME', 'login', {
      types: 'items',
      limit: 8,
    })
    expect(wrapper.findAll('[role="option"]').map((option) => option.text())).toEqual([
      'ACME-12Fix login',
      'ACME-24Login button',
    ])
  })

  it('keeps a newer query when an older response arrives late', async () => {
    const older = deferred()
    const newer = deferred()
    vi.mocked(searchProject).mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise)
    await mountEditor()
    await typeText('#log')
    await searchReady()
    await typeText('in')
    await searchReady()
    newer.resolve({ ...tickets, items: [tickets.items[1]!] })
    await flushPromises()
    older.resolve({ ...tickets, items: [tickets.items[0]!] })
    await flushPromises()

    expect(wrapper.get('[role="option"]').text()).toContain('ACME-24')
    expect(wrapper.text()).not.toContain('ACME-12')
  })

  it('Escape closes suggestions and ignores their outstanding response', async () => {
    const pending = deferred()
    vi.mocked(searchProject).mockReturnValue(pending.promise)
    await mountEditor()
    await typeText('#login')
    await searchReady()
    await wrapper.get('.tiptap').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
    pending.resolve(tickets)
    await flushPromises()
    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
    expect(editor.getMarkdown()).toBe('#login')
  })

  it('cancels the debounce when Escape closes the list before the request', async () => {
    await mountEditor()
    await typeText('#login')
    await wrapper.get('.tiptap').trigger('keydown', { key: 'Escape' })
    await vi.advanceTimersByTimeAsync(500)

    expect(searchProject).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
  })

  it('ignores outstanding results when the project context changes', async () => {
    const pending = deferred()
    vi.mocked(searchProject).mockReturnValueOnce(pending.promise)
    await mountEditor()
    await typeText('#login')
    await searchReady()
    await wrapper.setProps({ projectKey: 'OTHER' })
    pending.resolve(tickets)
    await flushPromises()

    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
    vi.mocked(searchProject).mockResolvedValue({ items: [], comments: [], pages: [] })
    await typeText('s')
    await searchReady()
    expect(searchProject).toHaveBeenLastCalledWith('acme', 'OTHER', 'logins', {
      types: 'items',
      limit: 8,
    })
    expect(wrapper.get('[data-testid="ticket-list"]').text()).toBe('No matches')
  })

  it('shows an empty result without turning Enter into a ticket insertion', async () => {
    vi.mocked(searchProject).mockResolvedValue({ items: [], comments: [], pages: [] })
    await mountEditor()
    await typeText('#missing')
    await searchReady()
    expect(wrapper.get('[data-testid="ticket-list"]').text()).toBe('No matches')
    await wrapper.get('.tiptap').trigger('keydown', { key: 'Enter' })

    expect(editor.state.doc.childCount).toBe(2)
    expect(editor.state.doc.textContent).toBe('#missing')
    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
  })

  it('reports failed searches while keeping the typed query intact', async () => {
    vi.mocked(searchProject).mockRejectedValue(new Error('Unavailable'))
    await mountEditor()
    await typeText('#login')
    await searchReady()

    expect(wrapper.get('[data-testid="ticket-list"]').text()).toBe('Could not load tickets.')
    expect(editor.getMarkdown()).toBe('#login')
  })

  it.each(['Enter', 'Tab'])(
    'inserts the selected key with %s and keeps surrounding text',
    async (key) => {
      await mountEditor({ modelValue: 'Before  after' })
      editor.commands.setTextSelection(8)
      await typeText('#login')
      await searchReady()
      await wrapper.get('.tiptap').trigger('keydown', { key: 'ArrowDown' })
      expect(wrapper.findAll('[role="option"]')[1]!.attributes('aria-selected')).toBe('true')
      await wrapper.get('.tiptap').trigger('keydown', { key })

      expect(editor.getMarkdown()).toBe('Before #ACME-24  after')
      expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['Before #ACME-24  after'])
      expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
    },
  )

  it('inserts a ticket by mouse without losing editor focus', async () => {
    await mountEditor()
    await typeText('#login')
    await searchReady()
    await wrapper.findAll('[role="option"]')[1]!.trigger('mousedown')

    expect(editor.getMarkdown()).toBe('#ACME-24 ')
    expect(editor.state.doc.textContent).toBe('#ACME-24 ')
    expect(editor.isFocused).toBe(true)
    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
  })

  it('the toolbar inserts a separated # and offers tickets', async () => {
    await mountEditor({ modelValue: 'See' })
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await wrapper.get('button[aria-label="Insert ticket reference"]').trigger('click')
    await searchReady()

    expect(editor.state.doc.textContent).toBe('See #')
    expect(searchProject).toHaveBeenCalledWith('acme', 'ACME', '', { types: 'items', limit: 8 })
    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(true)
  })

  it.each([
    ['inline code', '`#ACME-12`'],
    ['code block', '```\n#ACME-12\n```'],
    ['heading marker', '# Heading'],
    ['URL fragment', 'https://example.com/#ACME-12'],
    ['existing link', '[#ACME-12](/example)'],
  ])('does not suggest within %s', async (_kind, markdown) => {
    await mountEditor({ modelValue: markdown })
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await nextTick()
    await vi.advanceTimersByTimeAsync(500)

    expect(searchProject).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
  })

  it.each([{ slug: undefined }, { projectKey: undefined }])(
    'needs both organization and project context: %s',
    async (props) => {
      await mountEditor(props)
      await typeText('#login')
      await vi.advanceTimersByTimeAsync(500)

      expect(searchProject).not.toHaveBeenCalled()
      expect(wrapper.find('button[aria-label="Insert ticket reference"]').exists()).toBe(false)
      expect(wrapper.find('[data-testid="ticket-list"]').exists()).toBe(false)
    },
  )

  it('keeps @ mentions working without fetching tickets', async () => {
    await mountEditor({ mentionables: [{ id: 'u1', name: 'Ana Kovač' }] })
    await typeText('@Ana')
    expect(wrapper.get('[data-testid="mention-list"]').text()).toContain('Ana Kovač')
    await wrapper.get('.tiptap').trigger('keydown', { key: 'Enter' })
    await vi.advanceTimersByTimeAsync(500)

    expect(editor.state.doc.textContent).toBe('@AnaKovač ')
    expect(searchProject).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="mention-list"]').exists()).toBe(false)
  })
})

describe('ticket links in the Markdown editor', () => {
  async function mountLinked(modelValue: string) {
    vi.useRealTimers()
    vi.mocked(getItem).mockReset().mockImplementation(async (_slug, key) => {
      if (key === 'ACME-999') throw new Error('Not found')
      return { key } as WorkItem
    })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }] })
    await router.push('/')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    wrapper = mount(MarkdownEditor, {
      attachTo: document.body,
      props: { modelValue, slug: 'acme', projectKey: 'ACME', linkTickets: true },
      global: { plugins: [[VueQueryPlugin, { queryClient: client }], router] },
    })
    await flushPromises()
    editor = wrapper.findComponent(EditorContent).props('editor') as Editor
    return router
  }

  function click(target: Element, init: MouseEventInit = {}) {
    const event = new MouseEvent('click', { button: 0, ...init })
    Object.defineProperty(event, 'target', { value: target })
    return editor.view.someProp('handleClick', (handle) => handle(editor.view, 1, event))
  }

  it('links resolved project references without changing the Markdown', async () => {
    const router = await mountLinked('See #ACME-12, #ACME-999, #OTHER-1 and `#ACME-12`')
    expect(vi.mocked(getItem).mock.calls).toEqual([['acme', 'ACME-12'], ['acme', 'ACME-999']])
    const links = wrapper.findAll('a[data-ticket-link]')
    expect(links.map((link) => link.text())).toEqual(['#ACME-12'])
    expect(links[0]!.attributes('href')).toBe('/o/acme/p/ACME/items/ACME-12')
    expect(editor.getMarkdown()).toBe('See #ACME-12, #ACME-999, #OTHER-1 and `#ACME-12`')

    expect(click(links[0]!.element)).toBe(true)
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/o/acme/p/ACME/items/ACME-12')
  })

  it('links a reference once it is typed and opens modified clicks in a new tab', async () => {
    const router = await mountLinked('')
    editor.commands.insertContent('Blocked by #ACME-24')
    await wrapper.setProps({ modelValue: editor.getMarkdown() })
    await flushPromises()
    const link = wrapper.get('a[data-ticket-link]')
    expect(link.text()).toBe('#ACME-24')

    const open = vi.spyOn(window, 'open').mockReturnValue(null)
    expect(click(link.element, { ctrlKey: true })).toBe(true)
    expect(open).toHaveBeenCalledWith('/o/acme/p/ACME/items/ACME-24', '_blank', 'noopener')
    expect(router.currentRoute.value.path).toBe('/')
  })

  it('leaves references plain unless links are asked for', async () => {
    vi.mocked(getItem).mockReset()
    await mountEditor({ modelValue: '#ACME-12' })
    expect(getItem).not.toHaveBeenCalled()
    expect(wrapper.find('a[data-ticket-link]').exists()).toBe(false)
  })
})
