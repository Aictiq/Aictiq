import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ProjectChatMessage, ProjectChatView } from '@/api/project-chat'
import ProjectChatPanel from '@/components/ProjectChatPanel.vue'
import { useOrganizationsStore } from '@/stores/organizations'
import { ConflictError } from '@/utils/api'

const api = vi.hoisted(() => ({
  getProjectChat: vi.fn(),
  askProjectChat: vi.fn(),
  clearProjectChat: vi.fn(),
}))
vi.mock('@/api/project-chat', () => api)

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
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }],
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
    vi.fn(async () => new Response('{}', { status: 404, headers: { 'content-type': 'application/json' } })),
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

    expect(api.askProjectChat).toHaveBeenCalledWith('acme', 'PROJ', 'How does login work?')
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
    const queued = message({ status: 'pending', runStatus: 'queued', answer: null, answeredAt: null })
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

  it('shows why a question failed', async () => {
    api.getProjectChat.mockResolvedValue(
      chat([message({ status: 'timedOut', answer: null, error: 'The run timed out after 15 minutes.' })]),
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
