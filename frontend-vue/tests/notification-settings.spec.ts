import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ChatChannel, OrgChatChannel } from '@/api/notifications'
import type { Organization } from '@/api/organizations'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import { ApiError, ValidationError } from '@/utils/api'
import NotificationSettingsView from '@/views/settings/NotificationSettingsView.vue'
import OrgNotificationsView from '@/views/settings/OrgNotificationsView.vue'

const api = vi.hoisted(() => ({
  getNotificationPreferences: vi.fn(),
  putNotificationPreferences: vi.fn(),
  listChatChannels: vi.fn(),
  connectChatChannel: vi.fn(),
  testChatChannel: vi.fn(),
  disconnectChatChannel: vi.fn(),
  listOrgChatChannels: vi.fn(),
  connectOrgChatChannel: vi.fn(),
  updateOrgChatChannel: vi.fn(),
  testOrgChatChannel: vi.fn(),
  disconnectOrgChatChannel: vi.fn(),
  getOrgNotificationDefaults: vi.fn(),
  putOrgNotificationDefaults: vi.fn(),
}))
vi.mock('@/api/notifications', () => api)
const toast = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
  saved: vi.fn(),
  saveFailed: vi.fn(),
}))
vi.mock('@/composables/useToast', () => ({ useToast: () => toast }))

const channel = (over: Partial<ChatChannel>): ChatChannel => ({
  id: 'c1',
  type: 'slack',
  status: 'active',
  target: 'hooks.slack.com/…/a1b2',
  lastError: null,
  connectedAt: '2026-10-01T10:00:00Z',
  createdAt: '2026-10-01T10:00:00Z',
  ...over,
})

beforeEach(() => {
  Object.values(api).forEach((fn) => fn.mockReset())
  Object.values(toast).forEach((fn) => fn.mockReset())
})
afterEach(() => vi.useRealTimers())

describe('personal notification settings', () => {
  beforeEach(() => {
    api.getNotificationPreferences.mockResolvedValue([])
    api.putNotificationPreferences.mockImplementation(async (rows) => rows)
  })

  it('adds a column for a connected channel and sends null for Default', async () => {
    api.listChatChannels.mockResolvedValue({
      telegramAvailable: false,
      telegramBotUsername: null,
      channels: [channel({})],
    })
    const wrapper = mount(NotificationSettingsView)
    await flushPromises()

    expect(wrapper.find('[data-testid="channel-telegram"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="channel-slack"]').text()).toContain('hooks.slack.com/…/a1b2')
    expect(wrapper.get('thead').text()).toContain('Slack')
    expect(wrapper.get('thead').text()).not.toContain('Discord')
    expect(wrapper.text()).toContain('Run needs input')

    await wrapper.get('select[aria-label="Run failed on Slack"]').setValue('immediate')
    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Save preferences')!
      .trigger('click')
    const sent = api.putNotificationPreferences.mock.calls[0]![0] as Array<Record<string, unknown>>
    expect(sent.find((row) => row.kind === 'runFailed')).toMatchObject({ slack: 'immediate' })
    expect(sent.find((row) => row.kind === 'assigned')).toMatchObject({
      email: 'immediate',
      slack: null,
    })
  })

  it('shows the Telegram code and polls until the chat is connected', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const pending = channel({ id: 't1', type: 'telegram', status: 'pending', target: null })
    api.listChatChannels
      .mockResolvedValueOnce({
        telegramAvailable: true,
        telegramBotUsername: 'aictiq_bot',
        channels: [],
      })
      .mockResolvedValueOnce({
        telegramAvailable: true,
        telegramBotUsername: 'aictiq_bot',
        channels: [pending],
      })
      .mockResolvedValue({
        telegramAvailable: true,
        telegramBotUsername: 'aictiq_bot',
        channels: [{ ...pending, status: 'active', target: 'Chat ••••4821' }],
      })
    api.connectChatChannel.mockResolvedValue({
      channel: pending,
      connectCode: 'K7Q2',
      connectUrl: 'https://t.me/aictiq_bot?start=K7Q2',
      expiresAt: '2026-10-01T10:15:00Z',
    })
    const wrapper = mount(NotificationSettingsView)
    await flushPromises()

    const telegram = () => wrapper.get('[data-testid="channel-telegram"]')
    await telegram()
      .findAll('button')
      .find((b) => b.text() === 'Connect')!
      .trigger('click')
    await flushPromises()
    expect(api.connectChatChannel).toHaveBeenCalledWith({ type: 'telegram' })
    expect(telegram().text()).toContain('Send /start K7Q2 to @aictiq_bot')
    expect(telegram().get('a').attributes('href')).toBe('https://t.me/aictiq_bot?start=K7Q2')

    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(telegram().text()).toContain('Chat ••••4821')
    expect(telegram().find('[data-testid="telegram-connect-code"]').exists()).toBe(false)
    expect(wrapper.get('thead').text()).toContain('Telegram')
  })

  it('shows a webhook URL refusal inline and a failed test in the destination’s words', async () => {
    api.listChatChannels.mockResolvedValue({
      telegramAvailable: false,
      telegramBotUsername: null,
      channels: [
        channel({ id: 'd1', type: 'discord', status: 'broken', lastError: 'Unknown Webhook' }),
      ],
    })
    api.testChatChannel.mockRejectedValue(
      new ApiError(422, 'Request failed (422)', { error: 'Unknown Webhook' }),
    )
    api.connectChatChannel.mockRejectedValue(
      new ValidationError(400, 'Invalid', { errors: { webhookUrl: ['Not a Slack webhook URL.'] } }),
    )
    const wrapper = mount(NotificationSettingsView)
    await flushPromises()

    const slack = wrapper.get('[data-testid="channel-slack"]')
    await slack.get('input').setValue('https://example.com/nope')
    await slack.get('form').trigger('submit')
    await flushPromises()
    expect(slack.text()).toContain('Not a Slack webhook URL.')

    const discord = wrapper.get('[data-testid="channel-discord"]')
    await discord
      .findAll('button')
      .find((b) => b.text() === 'Send test')!
      .trigger('click')
    await flushPromises()
    expect(discord.get('[role="status"]').text()).toBe('Unknown Webhook')
  })

  it('shows a broken channel as active once a test gets through', async () => {
    api.listChatChannels
      .mockResolvedValueOnce({
        telegramAvailable: false,
        telegramBotUsername: null,
        channels: [
          channel({
            id: 'd1',
            type: 'discord',
            status: 'broken',
            lastError: 'Discord answered 500.',
          }),
        ],
      })
      .mockResolvedValue({
        telegramAvailable: false,
        telegramBotUsername: null,
        channels: [channel({ id: 'd1', type: 'discord', status: 'active' })],
      })
    api.testChatChannel.mockResolvedValue(undefined)
    const wrapper = mount(NotificationSettingsView)
    await flushPromises()
    expect(wrapper.get('[data-testid="channel-discord"]').text()).toContain('Broken')

    await wrapper
      .get('[data-testid="channel-discord"]')
      .findAll('button')
      .find((b) => b.text() === 'Send test')!
      .trigger('click')
    await flushPromises()

    const discord = wrapper.get('[data-testid="channel-discord"]')
    expect(discord.text()).toContain('Active')
    expect(discord.get('[role="status"]').text()).toBe('Test message sent.')
  })
})

describe('organization notification settings', () => {
  const organization = (role: Organization['role']): Organization => ({
    id: 'org-1',
    slug: 'acme',
    name: 'Acme',
    role,
    canOperateFactory: true,
    plan: 'trial',
    timeZone: 'UTC',
    weekStart: 'monday',
    membersCanCreateProjects: true,
    createdAt: '2026-01-01T00:00:00Z',
    version: 1,
  })
  const shared: OrgChatChannel = {
    ...channel({ id: 'o1' }),
    name: '#releases',
    modes: { runFailed: 'immediate' },
  }

  function mountOrg(role: Organization['role'] = 'admin') {
    const scope: OrgScope = {
      slug: ref('acme'),
      record: ref(organization(role)),
      loading: ref(false),
      notFound: ref(false),
      reload: async () => {},
      set: () => {},
    }
    return mount(OrgNotificationsView, {
      global: { plugins: [createPinia()], provide: { [orgScopeKey]: scope } },
    })
  }

  beforeEach(() => {
    api.listOrgChatChannels.mockResolvedValue({
      telegramAvailable: true,
      telegramBotUsername: 'aictiq_bot',
      kinds: ['runSucceeded', 'runFailed'],
      channels: [shared],
    })
    api.getOrgNotificationDefaults.mockResolvedValue([
      { kind: 'mentioned', email: 'digest', telegram: null, slack: null, discord: null },
    ])
    api.putOrgNotificationDefaults.mockImplementation(async (_slug, rows) => rows)
  })

  it('tells members the tab is for admins without calling the API', async () => {
    const wrapper = mountOrg('member')
    await flushPromises()
    expect(wrapper.text()).toContain('Only organization owners and admins')
    expect(api.listOrgChatChannels).not.toHaveBeenCalled()
  })

  it('saves a shared channel’s mode with PATCH', async () => {
    api.updateOrgChatChannel.mockImplementation(async (_slug, _id, body) => ({
      ...shared,
      ...body,
    }))
    const wrapper = mountOrg()
    await flushPromises()

    const card = wrapper.get('[data-testid="org-channel-o1"]')
    expect(card.text()).toContain('#releases')
    expect(
      (card.get('select[aria-label="Run failed on #releases"]').element as HTMLSelectElement).value,
    ).toBe('immediate')
    await card.get('select[aria-label="Run succeeded on #releases"]').setValue('digest')
    await flushPromises()
    expect(api.updateOrgChatChannel).toHaveBeenCalledWith('acme', 'o1', {
      modes: { runFailed: 'immediate', runSucceeded: 'digest' },
    })
  })

  it('shows the group instructions after adding a Telegram channel', async () => {
    const pending: OrgChatChannel = {
      ...shared,
      id: 'o2',
      type: 'telegram',
      status: 'pending',
      target: null,
      name: 'Team',
    }
    api.connectOrgChatChannel.mockResolvedValue({
      channel: pending,
      connectCode: 'G9X1',
      connectUrl: 'https://t.me/aictiq_bot?startgroup=G9X1',
      expiresAt: null,
    })
    const wrapper = mountOrg()
    await flushPromises()
    api.listOrgChatChannels.mockResolvedValue({
      telegramAvailable: true,
      telegramBotUsername: 'aictiq_bot',
      kinds: [],
      channels: [shared, pending],
    })

    await wrapper.get('select[aria-label="Channel type"]').setValue('telegram')
    await wrapper.get('input[aria-label="Channel name"]').setValue('Team')
    expect(wrapper.find('input[aria-label="Webhook URL"]').exists()).toBe(false)
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(api.connectOrgChatChannel).toHaveBeenCalledWith('acme', {
      type: 'telegram',
      name: 'Team',
      webhookUrl: undefined,
    })
    const card = wrapper.get('[data-testid="org-channel-o2"]')
    expect(card.text()).toContain('Add @aictiq_bot to the group and send /start G9X1 in it.')
    expect(card.text()).toContain('/start@aictiq_bot G9X1')
  })

  it('saves only the member defaults that were chosen', async () => {
    const wrapper = mountOrg()
    await flushPromises()

    const mentioned = wrapper.get('select[aria-label="Mentioned by Email"]')
    expect((mentioned.element as HTMLSelectElement).value).toBe('digest')
    await wrapper.get('select[aria-label="Run failed by Telegram"]').setValue('immediate')
    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Save defaults')!
      .trigger('click')
    await flushPromises()

    expect(api.putOrgNotificationDefaults).toHaveBeenCalledWith('acme', [
      { kind: 'mentioned', email: 'digest', telegram: null, slack: null, discord: null },
      { kind: 'runFailed', email: null, telegram: 'immediate', slack: null, discord: null },
    ])
  })
})
