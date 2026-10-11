import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { createItem } from '@/api/items'
import { listItemTemplates, type ItemTemplate } from '@/api/templates'
import CreateTicketDialog from '@/components/items/CreateTicketDialog.vue'
import { projectScopeKey } from '@/composables/useSettingsScope'
import ProjectTemplatesView from '@/views/settings/ProjectTemplatesView.vue'

vi.mock('@/api/templates', () => ({
  listItemTemplates: vi.fn(),
  createItemTemplate: vi.fn(),
  updateItemTemplate: vi.fn(),
  deleteItemTemplate: vi.fn(),
}))
vi.mock('@/api/items', () => ({ createItem: vi.fn(async () => ({ id: 'item', key: 'WEB-1' })) }))
vi.mock('@/api/labels', () => ({ listLabels: vi.fn(async () => []) }))

const bug: ItemTemplate = {
  id: 'bug',
  type: 'bug',
  name: 'Bug report',
  descriptionMarkdown: '## Steps to reproduce\n\n## Expected\n\n## Actual',
  defaultLabelIds: ['label'],
  defaultPriority: 'high',
  isDefault: true,
  version: 1,
}
const story: ItemTemplate = {
  ...bug,
  id: 'story',
  type: 'story',
  name: 'User story',
  descriptionMarkdown: '## Acceptance criteria',
  defaultLabelIds: [],
  defaultPriority: null,
}
const alternative: ItemTemplate = {
  ...bug,
  id: 'alternative',
  name: 'Short bug',
  descriptionMarkdown: '## Problem',
  defaultPriority: 'low',
  defaultLabelIds: [],
  isDefault: false,
}
const wrappers: ReturnType<typeof mount>[] = []
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(listItemTemplates).mockResolvedValue([bug, story, alternative])
})
afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
})

function mountDialog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const passthrough = { template: '<div><slot /></div>' }
  const wrapper = mount(CreateTicketDialog, {
    props: { slug: 'acme', projectKey: 'WEB', open: true, defaultType: 'bug' },
    global: {
      plugins: [createPinia(), [VueQueryPlugin, { queryClient: client }]],
      stubs: {
        Dialog: passthrough,
        DialogContent: passthrough,
        DialogHeader: passthrough,
        DialogTitle: passthrough,
        DialogDescription: passthrough,
      },
    },
  })
  wrappers.push(wrapper)
  return { wrapper, client }
}
async function create(wrapper: ReturnType<typeof mount>, title: string, type?: string) {
  if (type) await wrapper.get('#create-ticket-type').setValue(type)
  await wrapper.get('#create-ticket-title').setValue(title)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('ticket templates', () => {
  it('files a quick title with its type default template', async () => {
    const { wrapper } = mountDialog()
    await create(wrapper, 'Broken login')
    expect(createItem).toHaveBeenCalledWith(
      'acme',
      'WEB',
      expect.objectContaining({
        type: 'bug',
        title: 'Broken login',
        descriptionMarkdown: bug.descriptionMarkdown,
        priority: 'high',
        labelIds: ['label'],
      }),
    )
    expect(wrapper.emitted('created')?.[0]?.[0]).toMatchObject({ key: 'WEB-1' })
  })
  it('uses the default of the type picked, never a non-default template', async () => {
    const { wrapper } = mountDialog()
    await create(wrapper, 'Sign up', 'story')
    expect(createItem).toHaveBeenCalledWith(
      'acme',
      'WEB',
      expect.objectContaining({
        type: 'story',
        descriptionMarkdown: story.descriptionMarkdown,
        priority: undefined,
        labelIds: [],
      }),
    )
  })
  it('leaves a type with no template empty', async () => {
    const { wrapper } = mountDialog()
    await create(wrapper, 'An epic', 'epic')
    expect(createItem).toHaveBeenCalledWith(
      'acme',
      'WEB',
      expect.objectContaining({
        descriptionMarkdown: undefined,
        priority: undefined,
        labelIds: undefined,
      }),
    )
  })
  it('still creates the ticket when templates cannot be read', async () => {
    vi.mocked(listItemTemplates).mockRejectedValue(new Error('offline'))
    const { wrapper } = mountDialog()
    await create(wrapper, 'Broken login')
    expect(createItem).toHaveBeenCalledWith(
      'acme',
      'WEB',
      expect.objectContaining({ title: 'Broken login', descriptionMarkdown: undefined }),
    )
  })
})

describe('template settings permissions', () => {
  it.each([
    ['member', false, false],
    ['guest', false, false],
    ['admin', true, false],
    ['admin', false, true],
  ])('role %s, archived %s: editing allowed %s', async (role, isArchived, mayWrite) => {
    const wrapper = mount(ProjectTemplatesView, {
      global: {
        provide: {
          [projectScopeKey as symbol]: {
            slug: ref('acme'),
            projectKey: ref('WEB'),
            record: ref({ role, isArchived }),
          },
        },
      },
    })
    wrappers.push(wrapper)
    await flushPromises()
    expect(wrapper.text()).toContain('Bug report')
    expect(wrapper.find('form').exists()).toBe(mayWrite)
    expect(wrapper.findAll('button').some((button) => button.text() === 'Edit')).toBe(mayWrite)
    expect(wrapper.findAll('button').some((button) => button.text() === 'Delete')).toBe(mayWrite)
    if (!mayWrite) expect(wrapper.text()).toMatch(/read-only|Only project admins/)
  })
})
