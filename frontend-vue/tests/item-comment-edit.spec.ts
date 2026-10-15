import { flushPromises, mount, RouterLinkStub, type VueWrapper, type DOMWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'

import ItemDetail from '@/components/items/ItemDetail.vue'
import type { WorkItem } from '@/api/items'
import type { WorkItemComment } from '@/api/comments'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

vi.mock('@/composables/useProjectRealtime', () => ({ useProjectRealtime: vi.fn() }))

const MarkdownEditor = defineComponent({
  props: { modelValue: { type: String, default: '' }, disabled: Boolean },
  emits: ['update:modelValue'],
  setup(props, { emit, expose }) {
    const uploading = ref(false)
    expose({ uploading, setUploading: (value: boolean) => { uploading.value = value } })
    return () => h('textarea', {
      value: props.modelValue,
      disabled: props.disabled,
      onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLTextAreaElement).value),
    })
  },
})

const item: WorkItem = {
  id: 'i1',
  key: 'PROJ-1',
  type: 'story',
  title: 'Crash on save',
  stateId: 's1',
  descriptionMarkdown: '',
  descriptionHtml: '',
  stateCategory: 'active',
  priority: 'none',
  assigneeId: null,
  teamId: 'team-1',
  sprintId: null,
  parentId: null,
  points: null,
  estimateHours: null,
  remainingHours: null,
  completedHours: null,
  dueDate: null,
  version: 1,
  labels: [],
  updatedAt: '',
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

const comment = (overrides: Partial<WorkItemComment> = {}): WorkItemComment => ({
  id: 'own',
  author: { id: 'u1', displayName: 'U One', avatarKey: null, isAgent: false },
  bodyMarkdown: 'Original **comment**',
  bodyHtml: '<p>Original <strong>comment</strong></p>',
  createdAt: '2026-10-01T10:00:00Z',
  editedAt: null,
  deletedAt: null,
  mentions: [],
  reactions: [],
  revisions: [],
  canReact: true,
  parentCommentId: null,
  ...overrides,
})
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status,
  headers: { 'content-type': 'application/json' },
})
const wrappers: VueWrapper[] = []
const clients: QueryClient[] = []

async function mountDetail(options: {
  role?: 'admin' | 'member' | 'guest'
  archived?: boolean
  comments?: WorkItemComment[]
  update?: (body: { bodyMarkdown: string }) => Response | Promise<Response>
  remove?: () => Response | Promise<Response>
} = {}) {
  let entries = options.comments ?? [comment(), comment({
    id: 'reply',
    author: { id: 'u2', displayName: 'Another Person', avatarKey: null, isAgent: false },
    bodyMarkdown: 'Reply stays in the thread',
    parentCommentId: 'own',
  })]
  const itemWrites: string[] = []
  const writes: { method: string; url: string; body?: { bodyMarkdown: string } }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input).split('?')[0] ?? ''
    if (url.endsWith('/comments/own') && init?.method === 'PATCH') {
      const body = JSON.parse(String(init.body)) as { bodyMarkdown: string }
      writes.push({ method: 'PATCH', url, body })
      if (options.update) return options.update(body)
      entries = entries.map((entry) => entry.id === 'own'
        ? { ...entry, ...body, editedAt: '2026-10-02T10:00:00Z' } : entry)
      return response(entries.find((entry) => entry.id === 'own'))
    }
    if (url.endsWith('/comments/own') && init?.method === 'DELETE') {
      writes.push({ method: 'DELETE', url })
      if (options.remove) return options.remove()
      entries = entries.map((entry) => entry.id === 'own'
        ? { ...entry, bodyMarkdown: '', bodyHtml: '', deletedAt: '2026-10-02T10:00:00Z' } : entry)
      return new Response(null, { status: 204 })
    }
    if (/\/items\/PROJ-\d+$/.test(url)) {
      if (init?.method === 'PATCH') itemWrites.push(url)
      return response({ ...item, key: url.split('/').at(-1) })
    }
    if (url.endsWith('/comments')) return response({ items: entries })
    if (url.endsWith('/history')) return response({ items: [] })
    if (url.endsWith('/runs')) return response({ items: [], totalCount: 0 })
    if (url.endsWith('/projects/PROJ')) return response({
      id: 'pr1', key: 'PROJ', name: 'Prototype',
      role: options.role ?? 'member', isArchived: options.archived ?? false,
    })
    if (url.endsWith('/board')) return response({ columns: [] })
    return response([])
  }))
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/o/:slug/p/:projectKey/items/:itemKey', component: { template: '<div />' } }],
  })
  await router.push('/o/acme/p/PROJ/items/PROJ-1')
  await router.isReady()
  const organizations = useOrganizationsStore()
  organizations.organizations = [{
    id: 'org-1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory: false,
  }]
  organizations.select('acme')
  useSessionStore().set({
    id: 'u1', email: 'u1@acme.dev', firstName: 'U', lastName: 'One', fullName: 'U One',
    roles: [], isAgent: false, avatarKey: null, timeZone: null,
  })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  clients.push(client)
  const invalidate = vi.spyOn(client, 'invalidateQueries')
  const wrapper = mount(ItemDetail, {
    props: { slug: 'acme', projectKey: 'PROJ', itemKey: 'PROJ-1' },
    attachTo: document.body,
    global: {
      plugins: [pinia, router, [VueQueryPlugin, { queryClient: client }]],
      stubs: {
        RouterLink: RouterLinkStub, Teleport: { template: '<div><slot /></div>' }, MarkdownEditor,
        RefinementPanel: true, StartRunDialog: true, TimeTrackingPopover: true,
      },
    },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, writes, itemWrites, invalidate, client, router }
}

function button(scope: Pick<DOMWrapper<Element>, 'findAll'>, label: string) {
  const found = scope.findAll('button').find((entry) => entry.text() === label)
  expect(found, `button '${label}'`).toBeDefined()
  return found!
}
const own = (wrapper: VueWrapper) => wrapper.get('#comment-own')
const form = (wrapper: VueWrapper) => wrapper.get('[data-testid="edit-comment-form"]')
async function edit(wrapper: VueWrapper, draft?: string) {
  await button(own(wrapper), 'Edit').trigger('click')
  if (draft !== undefined) await form(wrapper).get('textarea').setValue(draft)
}

afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  clients.splice(0).forEach((client) => client.clear())
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('ItemDetail own comment editing', () => {
  it.each(['member', 'guest', 'admin'] as const)('only offers own comment actions to a %s', async (role) => {
    const { wrapper } = await mountDetail({ role })
    expect(button(own(wrapper), 'Edit').exists()).toBe(true)
    expect(button(own(wrapper), 'Delete').exists()).toBe(true)
    const other = wrapper.get('#comment-reply')
    expect(other.findAll('button').map((entry) => entry.text())).not.toContain('Edit')
    expect(other.findAll('button').map((entry) => entry.text())).not.toContain('Delete')
  })

  it.each([
    ['archived', true, null],
    ['deleted', false, '2026-10-02T10:00:00Z'],
  ] as const)('hides actions on %s comments', async (_, archived, deletedAt) => {
    const { wrapper } = await mountDetail({ archived, comments: [comment({ deletedAt })] })
    const labels = own(wrapper).findAll('button').map((entry) => entry.text())
    expect(labels).not.toContain('Edit')
    expect(labels).not.toContain('Delete')
  })

  it('starts with the original Markdown, discards cancellation, and saves an edited marker', async () => {
    const { wrapper, writes, invalidate } = await mountDetail()
    await edit(wrapper)
    expect(form(wrapper).get<HTMLTextAreaElement>('textarea').element.value).toBe('Original **comment**')
    await form(wrapper).get('textarea').setValue('Discarded draft')
    await button(form(wrapper), 'Cancel').trigger('click')
    expect(wrapper.find('[data-testid="edit-comment-form"]').exists()).toBe(false)
    expect(writes).toHaveLength(0)
    expect(own(wrapper).text()).toContain('Original comment')
    await edit(wrapper, '  Updated **comment**  ')
    await form(wrapper).trigger('submit')
    await flushPromises()
    expect(writes).toEqual([{
      method: 'PATCH', url: '/api/v1/orgs/acme/items/PROJ-1/comments/own',
      body: { bodyMarkdown: 'Updated **comment**' },
    }])
    expect(wrapper.find('[data-testid="edit-comment-form"]').exists()).toBe(false)
    expect(own(wrapper).text()).toContain('Updated comment')
    expect(own(wrapper).text()).toContain('(edited)')
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['acme', 'PROJ-1', 'comments'] })
  })

  it.each([['blank', '   '], ['too long', 'a'.repeat(20_001)]])('rejects %s drafts without a request', async (_, draft) => {
    const { wrapper, writes } = await mountDetail()
    await edit(wrapper, draft)
    expect(button(form(wrapper), 'Save').attributes('disabled')).toBeDefined()
    await form(wrapper).trigger('submit')
    await flushPromises()
    expect(form(wrapper).get('[role="alert"]').text()).toContain('1–20,000')
    expect(writes).toHaveLength(0)
  })

  it.each([
    [422, { errors: { bodyMarkdown: ['Comment was rejected.'] } }, 'Comment was rejected.'],
    [403, { detail: 'You can only edit your own comments.' }, 'You can only edit your own comments.'],
    [503, { detail: 'Please try saving again.' }, 'Please try saving again.'],
  ] as const)('retains the draft after a %s backend error', async (status, problem, message) => {
    const { wrapper, writes } = await mountDetail({
      update: () => response({ title: 'Save failed', status, ...problem }, status),
    })
    await edit(wrapper, 'Keep this draft')
    await form(wrapper).trigger('submit')
    await flushPromises()
    expect(form(wrapper).get<HTMLTextAreaElement>('textarea').element.value).toBe('Keep this draft')
    expect(form(wrapper).get('[role="alert"]').text()).toBe(message)
    expect(button(form(wrapper), 'Save').attributes('disabled')).toBeUndefined()
    await form(wrapper).trigger('submit')
    await flushPromises()
    expect(writes).toHaveLength(2)
  })

  it('ignores repeated saves and disables cancellation while saving', async () => {
    let finish!: (response: Response) => void
    const pending = new Promise<Response>((resolve) => { finish = resolve })
    const { wrapper, writes } = await mountDetail({ update: () => pending })
    await edit(wrapper, 'Pending draft')
    await form(wrapper).trigger('submit')
    await form(wrapper).trigger('keydown', { key: 'Enter', ctrlKey: true })
    await form(wrapper).trigger('keydown', { key: 'Enter', metaKey: true })
    expect(writes).toHaveLength(1)
    expect(form(wrapper).get('textarea').attributes('disabled')).toBeDefined()
    expect(button(form(wrapper), 'Cancel').attributes('disabled')).toBeDefined()
    finish(response(comment({ bodyMarkdown: 'Pending draft' })))
    await flushPromises()
  })

  it('closes an editor when the comment is deleted remotely', async () => {
    const { wrapper, client, writes } = await mountDetail()
    await edit(wrapper, 'Unsaved draft')
    client.setQueryData(['acme', 'PROJ-1', 'comments'], { items: [comment({ deletedAt: '2026-10-02T10:00:00Z' })] })
    await flushPromises()
    expect(wrapper.find('[data-testid="edit-comment-form"]').exists()).toBe(false)
    expect(own(wrapper).text()).toContain('This comment was deleted.')
    expect(writes).toHaveLength(0)
  })

  it('closes an editor when the project is archived remotely', async () => {
    const { wrapper, client, writes } = await mountDetail()
    await edit(wrapper, 'Unsaved draft')
    client.setQueryData(['acme', 'PROJ', 'project'], {
      id: 'pr1', key: 'PROJ', name: 'Prototype', role: 'member', isArchived: true,
    })
    await flushPromises()
    expect(wrapper.find('[data-testid="edit-comment-form"]').exists()).toBe(false)
    expect(own(wrapper).findAll('button').map((entry) => entry.text())).not.toContain('Edit')
    expect(writes).toHaveLength(0)
  })

  it('keeps a pending save scoped to its original item after navigation', async () => {
    let finish!: (response: Response) => void
    const pending = new Promise<Response>((resolve) => { finish = resolve })
    const { wrapper, writes, invalidate } = await mountDetail({ update: () => pending })
    await edit(wrapper, 'Old item draft')
    await form(wrapper).trigger('submit')
    await wrapper.setProps({ itemKey: 'PROJ-2' })
    await flushPromises()
    expect(wrapper.find('[data-testid="edit-comment-form"]').exists()).toBe(false)
    finish(response(comment({ bodyMarkdown: 'Old item draft' })))
    await flushPromises()
    expect(writes[0]?.url).toBe('/api/v1/orgs/acme/items/PROJ-1/comments/own')
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['acme', 'PROJ-1', 'comments'] })
    expect(own(wrapper).text()).toContain('Original comment')
    expect(own(wrapper).text()).not.toContain('Old item draft')
  })

  it('lets a guest save an edited comment while leaving without patching the item', async () => {
    const { wrapper, writes, itemWrites, router } = await mountDetail({ role: 'guest' })
    await edit(wrapper, 'Guest comment draft')
    const navigation = router.push('/o/acme/p/PROJ/items/PROJ-2')
    await flushPromises()
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.text()).toContain('Your comment has unsaved changes.')
    await button(dialog, 'Save').trigger('click')
    await flushPromises()
    await navigation
    expect(router.currentRoute.value.path).toBe('/o/acme/p/PROJ/items/PROJ-2')
    expect(writes).toEqual([{
      method: 'PATCH', url: '/api/v1/orgs/acme/items/PROJ-1/comments/own',
      body: { bodyMarkdown: 'Guest comment draft' },
    }])
    expect(itemWrites).toHaveLength(0)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('retains and saves the draft when a comments refetch pages its comment out', async () => {
    const { wrapper, client, writes } = await mountDetail()
    await edit(wrapper, 'Draft kept across pagination')
    client.setQueryData(['acme', 'PROJ-1', 'comments'], {
      items: [comment({ id: 'newer-comment', bodyMarkdown: 'New comment displaced the old page' })],
    })
    await flushPromises()
    expect(form(wrapper).get<HTMLTextAreaElement>('textarea').element.value).toBe('Draft kept across pagination')
    await form(wrapper).trigger('submit')
    await flushPromises()
    expect(writes).toEqual([{
      method: 'PATCH', url: '/api/v1/orgs/acme/items/PROJ-1/comments/own',
      body: { bodyMarkdown: 'Draft kept across pagination' },
    }])
    expect(own(wrapper).text()).toContain('Draft kept across pagination')
    expect(wrapper.find('[data-testid="edit-comment-form"]').exists()).toBe(false)
  })

  it('disables leave-dialog actions while its comment save is pending', async () => {
    let finish!: (response: Response) => void
    const pending = new Promise<Response>((resolve) => { finish = resolve })
    const { wrapper, writes, router } = await mountDetail({ update: () => pending })
    await edit(wrapper, 'Save before leaving')
    const navigation = router.push('/o/acme/p/PROJ/items/PROJ-2')
    await flushPromises()
    const dialog = wrapper.get('[role="dialog"]')
    await button(dialog, 'Save').trigger('click')
    await flushPromises()
    for (const label of ['Keep editing', 'Discard', 'Saving…']) {
      const action = button(dialog, label)
      expect(action.attributes('disabled')).toBeDefined()
      await action.trigger('click')
    }
    await dialog.trigger('keydown', { key: 'Escape' })
    await flushPromises()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(true)
    expect(router.currentRoute.value.path).toBe('/o/acme/p/PROJ/items/PROJ-1')
    expect(writes).toHaveLength(1)
    finish(response(comment({ bodyMarkdown: 'Save before leaving' })))
    await flushPromises()
    await navigation
    expect(router.currentRoute.value.path).toBe('/o/acme/p/PROJ/items/PROJ-2')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('guards leaving during an upload even before the Markdown changes', async () => {
    const { wrapper, writes, router } = await mountDetail()
    await edit(wrapper)
    const editor = form(wrapper).findComponent(MarkdownEditor).vm as unknown as {
      setUploading: (value: boolean) => void
    }
    editor.setUploading(true)
    await flushPromises()
    expect(button(form(wrapper), 'Save').attributes('disabled')).toBeDefined()
    expect(button(form(wrapper), 'Cancel').attributes('disabled')).toBeDefined()
    await form(wrapper).trigger('submit')
    expect(writes).toHaveLength(0)
    const navigation = router.push('/o/acme/p/PROJ/items/PROJ-2')
    await flushPromises()
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.text()).toContain('Your comment has unsaved changes.')
    expect(button(dialog, 'Save').attributes('disabled')).toBeDefined()
    await button(dialog, 'Save').trigger('click')
    expect(writes).toHaveLength(0)
    await button(dialog, 'Keep editing').trigger('click')
    await navigation
    expect(router.currentRoute.value.path).toBe('/o/acme/p/PROJ/items/PROJ-1')
    expect(form(wrapper).get<HTMLTextAreaElement>('textarea').element.value).toBe('Original **comment**')
    editor.setUploading(false)
    await flushPromises()
    expect(button(form(wrapper), 'Save').attributes('disabled')).toBeUndefined()
    expect(button(form(wrapper), 'Cancel').attributes('disabled')).toBeUndefined()
  })
})

describe('ItemDetail own comment deletion', () => {
  it('requires confirmation, supports cancellation, and keeps replies beneath the tombstone', async () => {
    const { wrapper, writes } = await mountDetail()
    await button(own(wrapper), 'Delete').trigger('click')
    await flushPromises()
    let dialog = wrapper.get('[role="dialog"]')
    expect(dialog.text()).toContain('Delete your comment?')
    expect(dialog.text()).toContain('Replies in the thread will stay.')
    expect(writes).toHaveLength(0)
    await button(dialog, 'Cancel').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(writes).toHaveLength(0)
    await button(own(wrapper), 'Delete').trigger('click')
    await flushPromises()
    dialog = wrapper.get('[role="dialog"]')
    await button(dialog, 'Delete comment').trigger('click')
    await flushPromises()
    expect(writes).toEqual([{ method: 'DELETE', url: '/api/v1/orgs/acme/items/PROJ-1/comments/own' }])
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(own(wrapper).text()).toContain('This comment was deleted.')
    expect(own(wrapper).text()).not.toContain('Original comment')
    const thread = wrapper.get('[data-testid="comment-thread"]')
    expect(thread.get('#comment-reply').text()).toContain('Reply stays in the thread')
    expect(thread.findAll('[data-testid="comment"]')).toHaveLength(2)
  })

  it('retains the confirmation and shows backend deletion errors', async () => {
    const { wrapper, writes } = await mountDetail({
      remove: () => response({ title: 'Delete failed', detail: 'Please try deleting again.', status: 503 }, 503),
    })
    await button(own(wrapper), 'Delete').trigger('click')
    await flushPromises()
    await button(wrapper.get('[role="dialog"]'), 'Delete comment').trigger('click')
    await flushPromises()
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.get('[role="alert"]').text()).toBe('Please try deleting again.')
    expect(button(dialog, 'Delete comment').attributes('disabled')).toBeUndefined()
    expect(own(wrapper).text()).toContain('Original comment')
    expect(writes).toHaveLength(1)
  })

  it('blocks repeated confirmation and cancellation during a pending deletion', async () => {
    let finish!: (response: Response) => void
    const pending = new Promise<Response>((resolve) => { finish = resolve })
    const { wrapper, writes } = await mountDetail({ remove: () => pending })
    await button(own(wrapper), 'Delete').trigger('click')
    await flushPromises()
    const dialog = wrapper.get('[role="dialog"]')
    const confirm = button(dialog, 'Delete comment')
    await confirm.trigger('click')
    await confirm.trigger('click')
    expect(writes).toHaveLength(1)
    expect(confirm.attributes('disabled')).toBeDefined()
    expect(button(dialog, 'Cancel').attributes('disabled')).toBeDefined()
    await button(dialog, 'Cancel').trigger('click')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(true)
    finish(new Response(null, { status: 204 }))
    await flushPromises()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })
})

