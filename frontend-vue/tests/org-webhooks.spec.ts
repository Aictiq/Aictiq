import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'

import { createWebhook, deleteWebhook, listWebhooks, type Webhook } from '@/api/webhooks'
import OrgWebhooksView from '@/views/settings/OrgWebhooksView.vue'

vi.mock('@/api/webhooks', () => ({
  createWebhook: vi.fn(),
  deleteWebhook: vi.fn(),
  listWebhooks: vi.fn(),
  listWebhookDeliveries: vi.fn(async () => []),
  redeliverWebhook: vi.fn(),
  testWebhook: vi.fn(),
}))
vi.mock('@/api/github', () => ({
  disconnectGitHubInstallation: vi.fn(),
  githubInstallUrl: vi.fn(),
  listGitHubDeliveries: vi.fn(async () => []),
  listGitHubInstallations: vi.fn(async () => []),
  reprocessGitHubDelivery: vi.fn(),
}))
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('@/composables/useToast', () => ({ useToast: () => toast }))

const secret = 'fixture-webhook-signing-secret-not-a-credential'
const hook: Webhook = {
  id: 'hook-1',
  projectId: null,
  url: 'https://receiver.example/aictiq',
  events: ['item.updated'],
  active: true,
  consecutiveFailures: 0,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
}
const wrappers: ReturnType<typeof mount>[] = []

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(listWebhooks).mockReset().mockResolvedValue([])
  vi.mocked(createWebhook).mockReset().mockResolvedValue({ subscription: hook, secret })
  vi.mocked(deleteWebhook).mockReset().mockResolvedValue(undefined)
})
afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  vi.restoreAllMocks()
})

function stubClipboard(clipboard: Pick<Clipboard, 'writeText'> | undefined) {
  vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue(clipboard as Clipboard)
}

async function mountWebhooks() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/org/:slug/webhooks', component: { template: '<div />' } }],
  })
  await router.push('/org/acme/webhooks')
  const wrapper = mount(OrgWebhooksView, { global: { plugins: [router] } })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, router }
}

async function create(wrapper: ReturnType<typeof mount>) {
  await wrapper.get('input[type="url"]').setValue(hook.url)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

function button(wrapper: ReturnType<typeof mount>, text: string) {
  return wrapper.findAll('button').find((candidate) => candidate.text() === text)!
}

describe('one-time webhook signing secret', () => {
  it('preserves the secret without a clipboard and offers manual copying after Copy fails', async () => {
    stubClipboard(undefined)
    const { wrapper } = await mountWebhooks()

    await create(wrapper)

    expect(createWebhook).toHaveBeenCalledWith('acme', {
      url: hook.url,
      events: ['item.updated'],
    })
    expect(wrapper.get('[role="status"] code').text()).toBe(secret)
    expect(wrapper.get('[role="status"]').text()).toContain(
      'Copy it now - this is the only time it is shown.',
    )
    expect((wrapper.get('input[type="url"]').element as HTMLInputElement).value).toBe('')
    expect(listWebhooks).toHaveBeenCalledTimes(2)
    expect(toast.error).not.toHaveBeenCalled()
    expect(toast.success).not.toHaveBeenCalledWith(expect.stringMatching(/copied/i))

    await wrapper.get('button[aria-label="Copy the webhook secret"]').trigger('click')
    await flushPromises()

    expect(toast.error).toHaveBeenCalledWith(
      expect.any(Error),
      'Could not copy - select the secret and copy it manually.',
    )
    expect(wrapper.get('[role="status"] code').text()).toBe(secret)
  })

  it('retains the displayed secret when clipboard permission is denied', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('Clipboard permission denied'))
    stubClipboard({ writeText })
    const { wrapper } = await mountWebhooks()
    await create(wrapper)
    expect(writeText).not.toHaveBeenCalled()

    await wrapper.get('button[aria-label="Copy the webhook secret"]').trigger('click')
    await flushPromises()

    expect(writeText).toHaveBeenCalledWith(secret)
    expect(toast.error).toHaveBeenCalledWith(
      expect.any(Error),
      'Could not copy - select the secret and copy it manually.',
    )
    expect(wrapper.get('[role="status"] code').text()).toBe(secret)
    expect(createWebhook).toHaveBeenCalledTimes(1)
  })

  it('copies only on request and leaves the secret visible after a successful copy', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    stubClipboard({ writeText })
    const { wrapper } = await mountWebhooks()
    await create(wrapper)
    expect(writeText).not.toHaveBeenCalled()

    await wrapper.get('button[aria-label="Copy the webhook secret"]').trigger('click')
    await flushPromises()

    expect(writeText).toHaveBeenCalledExactlyOnceWith(secret)
    expect(toast.success).toHaveBeenCalledWith('Secret copied')
    expect(wrapper.get('[role="status"] code').text()).toBe(secret)
  })

  it('requires dismissal before another webhook can replace the one-time secret', async () => {
    const { wrapper } = await mountWebhooks()
    await create(wrapper)
    expect(button(wrapper, 'Create').attributes('disabled')).toBeDefined()

    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(createWebhook).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[role="status"] code').text()).toBe(secret)

    await button(wrapper, 'Done').trigger('click')

    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain(secret)
    expect(button(wrapper, 'Create').attributes('disabled')).toBeUndefined()
  })

  it('prevents duplicate creation while the first request is pending', async () => {
    let resolve!: (created: { subscription: Webhook; secret: string }) => void
    vi.mocked(createWebhook).mockImplementationOnce(
      () =>
        new Promise((done) => {
          resolve = done
        }),
    )
    const { wrapper } = await mountWebhooks()
    await create(wrapper)
    expect(button(wrapper, 'Create').attributes('disabled')).toBeDefined()

    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(createWebhook).toHaveBeenCalledTimes(1)

    resolve({ subscription: hook, secret })
    await flushPromises()
    expect(wrapper.get('[role="status"] code').text()).toBe(secret)
  })

  it('keeps the secret available if refreshing the webhook list fails', async () => {
    const { wrapper } = await mountWebhooks()
    const error = new Error('List temporarily unavailable')
    vi.mocked(listWebhooks).mockRejectedValueOnce(error)

    await create(wrapper)

    expect(toast.error).toHaveBeenCalledWith(error)
    expect(wrapper.get('[role="status"] code').text()).toBe(secret)
  })

  it('does not show a secret when creating the webhook fails and allows retrying', async () => {
    vi.mocked(createWebhook).mockRejectedValueOnce(new Error('Creation failed'))
    const { wrapper } = await mountWebhooks()

    await create(wrapper)

    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(toast.error).toHaveBeenCalledWith(new Error('Creation failed'))
    expect(button(wrapper, 'Create').attributes('disabled')).toBeUndefined()
    expect((wrapper.get('input[type="url"]').element as HTMLInputElement).value).toBe(hook.url)
  })

  it('clears the secret when changing organizations', async () => {
    const { wrapper, router } = await mountWebhooks()
    await create(wrapper)

    await router.push('/org/another-org/webhooks')
    await flushPromises()

    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain(secret)
    expect(listWebhooks).toHaveBeenLastCalledWith('another-org')
  })

  it('does not display a pending creation response after changing organizations', async () => {
    let resolve!: (created: { subscription: Webhook; secret: string }) => void
    vi.mocked(createWebhook).mockImplementationOnce(
      () =>
        new Promise((done) => {
          resolve = done
        }),
    )
    const { wrapper, router } = await mountWebhooks()
    await create(wrapper)

    await router.push('/org/another-org/webhooks')
    await flushPromises()
    await wrapper.get('input[type="url"]').setValue('https://another-receiver.example/aictiq')
    resolve({ subscription: hook, secret })
    await flushPromises()

    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain(secret)
    expect((wrapper.get('input[type="url"]').element as HTMLInputElement).value).toBe(
      'https://another-receiver.example/aictiq',
    )
    expect(toast.success).not.toHaveBeenCalled()
    expect(listWebhooks).toHaveBeenCalledTimes(2)
    expect(listWebhooks).toHaveBeenLastCalledWith('another-org')
    expect(button(wrapper, 'Create').attributes('disabled')).toBeUndefined()
  })

  it('clears the one-time panel when its webhook is deleted', async () => {
    vi.mocked(listWebhooks).mockResolvedValueOnce([]).mockResolvedValue([hook])
    const { wrapper } = await mountWebhooks()
    await create(wrapper)

    await button(wrapper, 'Delete').trigger('click')
    await flushPromises()

    expect(deleteWebhook).toHaveBeenCalledWith('acme', hook.id)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain(secret)
  })
})
