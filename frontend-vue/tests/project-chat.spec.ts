import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ProjectChatMessage, ProjectChatView } from '@/api/project-chat'
import ProjectChatPanel from '@/components/ProjectChatPanel.vue'
import { useOrganizationsStore } from '@/stores/organizations'
import { ConflictError, ValidationError } from '@/utils/api'

const api = vi.hoisted(() => ({
  getProjectChat: vi.fn(),
  askProjectChat: vi.fn(),
  clearProjectChat: vi.fn(),
}))
vi.mock('@/api/project-chat', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/project-chat')>()),
  ...api,
}))
const files = vi.hoisted(() => ({
  uploadAttachment: vi.fn(),
  deleteAttachment: vi.fn(),
}))
vi.mock('@/api/attachments', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/attachments')>()),
  ...files,
}))

/**
 * The project chat: an operator asks the project's agent a question, sees it queued or
 * running, and reads the answer. Stakeholders are not offered it at all.
 */
const message = (extra: Partial<ProjectChatMessage> = {}): ProjectChatMessage => ({
  id: 'm1',
  question: 'How does login work?',
  answer: 'It uses **cookies**. See [PROJ-3](/o/acme/p/PROJ/items/PROJ-3).',
  status: 'answered',
  runStatus: null,
  error: null,
  runId: 'r1',
  askedAt: '2026-10-18T10:00:00Z',
  answeredAt: '2026-10-18T10:01:00Z',
  attachments: [],
  ...extra,
})

const chat = (messages: ProjectChatMessage[], runnerOnline = true): ProjectChatView => ({
  messages,
  runnerOnline,
})

async function mountPanel(canOperateFactory = true) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const organizations = useOrganizationsStore()
  organizations.organizations = [
    { id: 'org-1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory } as never,
  ]
  organizations.select('acme')
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:rest(.*)*', component: { template: '<div />' } }],
  })
  const wrapper = mount(ProjectChatPanel, {
    props: { slug: 'acme', projectKey: 'PROJ' },
    attachTo: document.body,
    global: {
      plugins: [
        pinia,
        router,
        [
          VueQueryPlugin,
          { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) },
        ],
      ],
    },
  })
  return wrapper
}

async function openPanel(wrapper: Awaited<ReturnType<typeof mountPanel>>) {
  await wrapper.find('[data-testid="project-chat-button"]').trigger('click')
  await flushPromises()
}

beforeEach(() => {
  vi.resetAllMocks()
  // Answers to `#KEY` lookups the Markdown renderer makes; nothing here depends on them.
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response('{}', { status: 404, headers: { 'content-type': 'application/json' } }),
    ),
  )
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  document.body.innerHTML = ''
})

describe('ProjectChatPanel', () => {
  it('renders nothing for someone who cannot operate the factory', async () => {
    const wrapper = await mountPanel(false)

    expect(wrapper.find('[data-testid="project-chat-button"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="project-chat-panel"]').exists()).toBe(false)
    expect(api.getProjectChat).not.toHaveBeenCalled()
  })

  it('loads the history when opened and renders the answer as Markdown', async () => {
    api.getProjectChat.mockResolvedValue(chat([message()]))
    const wrapper = await mountPanel()
    expect(api.getProjectChat).not.toHaveBeenCalled()

    await openPanel(wrapper)

    expect(api.getProjectChat).toHaveBeenCalledWith('acme', 'PROJ')
    const messages = wrapper.findAll('[data-testid="project-chat-message"]')
    expect(messages).toHaveLength(1)
    expect(messages[0]!.text()).toContain('How does login work?')
    expect(messages[0]!.find('strong').text()).toBe('cookies')
    expect(messages[0]!.find('a').attributes('href')).toBe('/o/acme/p/PROJ/items/PROJ-3')
  })

  it('sends a question with Enter and shows it running', async () => {
    api.getProjectChat.mockResolvedValue(chat([]))
    api.askProjectChat.mockResolvedValue(
      chat([message({ status: 'pending', runStatus: 'running', answer: null, answeredAt: null })]),
    )
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    const input = wrapper.find('[data-testid="project-chat-input"]')
    await input.setValue('  How does login work?  ')
    await input.trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(api.askProjectChat).toHaveBeenCalledWith('acme', 'PROJ', 'How does login work?', [])
    expect((input.element as HTMLTextAreaElement).value).toBe('')
    expect(wrapper.find('[data-testid="project-chat-running"]').text()).toContain('Running')
    expect(wrapper.find('[data-testid="project-chat-send"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="project-chat-no-runner"]').exists()).toBe(false)
  })

  it('does not send on Shift+Enter', async () => {
    api.getProjectChat.mockResolvedValue(chat([]))
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    const input = wrapper.find('[data-testid="project-chat-input"]')
    await input.setValue('First line')
    await input.trigger('keydown', { key: 'Enter', shiftKey: true })
    await flushPromises()

    expect(api.askProjectChat).not.toHaveBeenCalled()
  })

  it('says no runner is online for a queued question, and polls until it is answered', async () => {
    vi.useFakeTimers()
    const queued = message({
      status: 'pending',
      runStatus: 'queued',
      answer: null,
      answeredAt: null,
    })
    api.getProjectChat
      .mockResolvedValueOnce(chat([queued], false))
      .mockResolvedValueOnce(chat([message()], true))
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    expect(wrapper.find('[data-testid="project-chat-running"]').text()).toContain('Queued')
    expect(wrapper.find('[data-testid="project-chat-no-runner"]').text()).toBe(
      'No runner is online. Your question will start when a runner connects.',
    )

    await vi.advanceTimersByTimeAsync(2500)
    await flushPromises()

    expect(api.getProjectChat).toHaveBeenCalledTimes(2)
    expect(wrapper.find('[data-testid="project-chat-running"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('cookies')

    // Nothing pending: no more polling.
    await vi.advanceTimersByTimeAsync(10_000)
    expect(api.getProjectChat).toHaveBeenCalledTimes(2)
  })

  it('names the default runner when it is the one that is offline', async () => {
    const queued = message({
      status: 'pending',
      runStatus: 'queued',
      answer: null,
      answeredAt: null,
    })
    api.getProjectChat.mockResolvedValue({ ...chat([queued], false), runnerName: 'hetzner-vm' })
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    expect(wrapper.find('[data-testid="project-chat-no-runner"]').text()).toBe(
      "hetzner-vm, this project's default runner, is offline. Your question will start when it connects.",
    )
  })

  it('shows why a question failed', async () => {
    api.getProjectChat.mockResolvedValue(
      chat([
        message({ status: 'timedOut', answer: null, error: 'The run timed out after 15 minutes.' }),
      ]),
    )
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    const failure = wrapper.find('[data-testid="project-chat-message-error"]')
    expect(failure.text()).toContain('Timed out.')
    expect(failure.text()).toContain('The run timed out after 15 minutes.')
  })

  it('shows a refused question inline', async () => {
    api.getProjectChat.mockResolvedValue(chat([]))
    api.askProjectChat.mockRejectedValue(
      new ConflictError(409, 'A question is already in progress.', {
        type: 'https://aictiq.dev/problems/conflict',
      }),
    )
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    await wrapper.find('[data-testid="project-chat-input"]').setValue('Another one')
    await wrapper.find('[data-testid="project-chat-send"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="project-chat-error"]').text()).toBe(
      'A question is already in progress.',
    )
    // The question in progress elsewhere is read so it shows up here.
    expect(api.getProjectChat).toHaveBeenCalledTimes(2)
  })

  async function pick(wrapper: Awaited<ReturnType<typeof mountPanel>>, ...picked: File[]) {
    const input = wrapper.find('[data-testid="project-chat-file-input"]')
    Object.defineProperty(input.element, 'files', { value: picked, configurable: true })
    await input.trigger('change')
    await flushPromises()
  }

  const file = (name: string, type: string) => new File(['content'], name, { type })

  it('uploads picked files for chat, lets one be removed and sends the rest with the question', async () => {
    api.getProjectChat.mockResolvedValue(chat([]))
    files.uploadAttachment.mockImplementation(
      async (_slug, _key, picked: File) => `id-${picked.name}`,
    )
    files.deleteAttachment.mockResolvedValue(undefined)
    api.askProjectChat.mockResolvedValue(
      chat([message({ status: 'pending', runStatus: 'queued', answer: null, answeredAt: null })]),
    )
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    await pick(wrapper, file('spec.pdf', 'application/pdf'), file('screens.zip', 'application/zip'))

    expect(files.uploadAttachment).toHaveBeenCalledWith('acme', 'PROJ', expect.any(File), 'chat')
    const selected = wrapper.findAll('[data-testid="project-chat-selected-file"]')
    expect(selected.map((s) => s.attributes('data-status'))).toEqual(['ready', 'ready'])

    await selected[0]!.find('[data-testid="project-chat-remove-file"]').trigger('click')
    expect(files.deleteAttachment).toHaveBeenCalledWith('acme', 'id-spec.pdf')

    await wrapper.find('[data-testid="project-chat-input"]').setValue('What is in the archive?')
    await wrapper.find('[data-testid="project-chat-send"]').trigger('click')
    await flushPromises()

    expect(api.askProjectChat).toHaveBeenCalledWith('acme', 'PROJ', 'What is in the archive?', [
      'id-screens.zip',
    ])
    expect(wrapper.find('[data-testid="project-chat-selected-files"]').exists()).toBe(false)
  })

  it('explains a refused upload and holds the question until the file is removed', async () => {
    api.getProjectChat.mockResolvedValue(chat([]))
    files.uploadAttachment.mockRejectedValue(
      new ValidationError(400, 'One or more validation errors occurred.', {
        errors: { file: ['broken.zip is not a readable ZIP archive.'] },
      }),
    )
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    await pick(wrapper, file('broken.zip', 'application/zip'))
    await wrapper.find('[data-testid="project-chat-input"]').setValue('Read this')

    expect(wrapper.find('[data-testid="project-chat-file-error"]').text()).toBe(
      'broken.zip: broken.zip is not a readable ZIP archive.',
    )
    expect(wrapper.find('[data-testid="project-chat-send"]').attributes('disabled')).toBeDefined()

    await wrapper.find('[data-testid="project-chat-remove-file"]').trigger('click')
    expect(files.deleteAttachment).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="project-chat-send"]').attributes('disabled')).toBeUndefined()
  })

  it('keeps the question and the files when the question is refused', async () => {
    api.getProjectChat.mockResolvedValue(chat([]))
    files.uploadAttachment.mockResolvedValue('id-1')
    api.askProjectChat.mockRejectedValue(
      new ValidationError(400, 'One or more validation errors occurred.', {
        errors: { attachmentIds: ['A selected file is no longer uploaded.'] },
      }),
    )
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    await pick(wrapper, file('photo.png', 'image/png'))
    const input = wrapper.find('[data-testid="project-chat-input"]')
    await input.setValue('What does this show?')
    await wrapper.find('[data-testid="project-chat-send"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="project-chat-error"]').text()).toBe(
      'A selected file is no longer uploaded.',
    )
    expect((input.element as HTMLTextAreaElement).value).toBe('What does this show?')
    expect(wrapper.findAll('[data-testid="project-chat-selected-file"]')).toHaveLength(1)
  })

  it('shows the files sent with each question in the history', async () => {
    api.getProjectChat.mockResolvedValue(
      chat([
        message({
          attachments: [
            { id: 'f1', fileName: 'diagram.webp', contentType: 'image/webp', sizeBytes: 2048 },
            { id: 'f2', fileName: 'spec.pdf', contentType: 'application/pdf', sizeBytes: 4096 },
          ],
        }),
      ]),
    )
    const wrapper = await mountPanel()
    await openPanel(wrapper)

    const sent = wrapper.findAll('[data-testid="project-chat-message-file"]')
    expect(sent.map((a) => a.text())).toEqual(['diagram.webp', 'spec.pdf'])
    expect(sent[1]!.attributes('href')).toBe('/api/v1/orgs/acme/attachments/f2/download')
    expect(sent[0]!.find('img').attributes('src')).toBe('/api/v1/orgs/acme/attachments/f1/download')
  })

  it('clears the history', async () => {
    api.getProjectChat.mockResolvedValue(chat([message(), message({ id: 'm2' })]))
    api.clearProjectChat.mockResolvedValue(undefined)
    const wrapper = await mountPanel()
    await openPanel(wrapper)
    expect(wrapper.findAll('[data-testid="project-chat-message"]')).toHaveLength(2)

    await wrapper.find('[data-testid="project-chat-clear"]').trigger('click')
    await flushPromises()

    expect(api.clearProjectChat).toHaveBeenCalledWith('acme', 'PROJ')
    expect(wrapper.findAll('[data-testid="project-chat-message"]')).toHaveLength(0)
  })
})
