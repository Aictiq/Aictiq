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
vi.mock('@/api/refinement', () => ({
  getRefinementSettings: vi.fn(async () => ({ enabled: false })),
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
        DialogFooter: passthrough,
        MarkdownEditor: {
          props: ['modelValue'],
          emits: ['update:modelValue'],
          template:
            '<textarea :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />',
        },
      },
    },
  })
  wrappers.push(wrapper)
  return { wrapper, client }
}
const picker = 'select[aria-label="Choose an item template"]'
const value = (wrapper: ReturnType<typeof mount>, selector = 'textarea') =>
  (wrapper.get(selector).element as HTMLInputElement).value

describe('ticket templates', () => {
  it('preselects the type default, filters the picker, and swaps untouched prefills', async () => {
    const { wrapper } = mountDialog()
    await flushPromises()
    expect(value(wrapper, picker)).toBe('bug')
    expect(wrapper.get(picker).text()).not.toContain('User story')
    expect(value(wrapper)).toBe(bug.descriptionMarkdown)
    await wrapper.get(picker).setValue('alternative')
    expect(value(wrapper)).toBe(alternative.descriptionMarkdown)
    await wrapper.get('#create-ticket-type').setValue('story')
    expect(value(wrapper, picker)).toBe('story')
    expect(value(wrapper)).toBe(story.descriptionMarkdown)
  })
  it('preserves edited descriptions when picking another template or changing type', async () => {
    const { wrapper } = mountDialog()
    await flushPromises()
    await wrapper.get('textarea').setValue('My reproduction steps')
    await wrapper.get(picker).setValue('alternative')
    await wrapper.get('#create-ticket-type').setValue('story')
    expect(value(wrapper)).toBe('My reproduction steps')
  })
  it('preserves text entered while templates are still loading', async () => {
    let resolve!: (templates: ItemTemplate[]) => void
    vi.mocked(listItemTemplates).mockReturnValue(
      new Promise((done) => {
        resolve = done
      }),
    )
    const { wrapper } = mountDialog()
    await wrapper.get('textarea').setValue('Already typed')
    resolve([bug])
    await flushPromises()
    expect(value(wrapper, picker)).toBe('bug')
    expect(value(wrapper)).toBe('Already typed')
  })
  it('sends priority and label defaults with the created ticket', async () => {
    const { wrapper } = mountDialog()
    await flushPromises()
    await wrapper.get('#create-ticket-title').setValue('Broken login')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(createItem).toHaveBeenCalledWith(
      'acme',
      'WEB',
      expect.objectContaining({
        descriptionMarkdown: bug.descriptionMarkdown,
        priority: 'high',
        labelIds: ['label'],
      }),
    )
  })
  it('leaves a type with no template empty and clears previous defaults', async () => {
    const { wrapper } = mountDialog()
    await flushPromises()
    await wrapper.get('#create-ticket-type').setValue('epic')
    expect(wrapper.find(picker).exists()).toBe(false)
    expect(value(wrapper)).toBe('')
    await wrapper.get('#create-ticket-title').setValue('An epic')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(createItem).toHaveBeenCalledWith(
      'acme',
      'WEB',
      expect.objectContaining({ priority: undefined, labelIds: undefined }),
    )
  })
  it('keeps a manual choice after refetch and resets it when reopening', async () => {
    const { wrapper, client } = mountDialog()
    await flushPromises()
    await wrapper.get(picker).setValue('alternative')
    await client.invalidateQueries()
    await flushPromises()
    expect(value(wrapper, picker)).toBe('alternative')
    await wrapper.setProps({ open: false })
    await wrapper.setProps({ open: true })
    await flushPromises()
    expect(value(wrapper, picker)).toBe('bug')
    expect(value(wrapper)).toBe(bug.descriptionMarkdown)
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
