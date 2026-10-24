import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { Notification, NotificationKind } from '@/api/notifications'
import NotificationRow from '@/components/notifications/NotificationRow.vue'
import { notificationAction, relativeTime } from '@/utils/notifications'

const ana = { id: 'u1', displayName: 'Ana Kovač', avatarKey: null, isAgent: false }
function entry(overrides: Partial<Notification> = {}): Notification {
  return {
    id: 'n1',
    organizationId: 'org',
    kind: 'commented',
    projectId: 'p1',
    itemId: 'i1',
    itemKey: 'WEB-7',
    message: 'A watched item has a new comment.',
    createdAt: new Date(Date.now() - 18 * 3600 * 1000).toISOString(),
    readAt: null,
    actor: ana,
    itemTitle: 'Fix the login',
    itemType: 'story',
    itemState: 'In Review',
    ...overrides,
  }
}

describe('notification headline', () => {
  it.each<[NotificationKind, Partial<Notification>, string]>([
    ['assigned', {}, 'assigned a story to you'],
    ['mentioned', {}, 'mentioned you'],
    ['commented', { itemType: 'bug' }, 'commented on a bug'],
    ['replied', {}, 'replied to your comment'],
    ['reacted', { emoji: '🎉' }, 'reacted 🎉 to your comment'],
    [
      'transitioned',
      { fromState: 'In Progress', toState: 'Review' },
      'moved a story from In Progress to Review',
    ],
    ['claimed', { itemType: 'epic' }, 'claimed an epic'],
    ['runSucceeded', {}, '’s run succeeded'],
    ['runFailed', { message: 'The run on WEB-7 timed out.' }, '’s run timed out'],
    ['runFailed', {}, '’s run failed'],
    ['runNeedsInput', {}, '’s refine run needs your input'],
    ['runRefined', {}, 'refined a story, ready for review'],
    ['sprintStarted', {}, 'started a sprint'],
    ['sprintCompleted', {}, 'completed a sprint'],
    ['wikiMentioned', {}, 'mentioned you on a wiki page'],
    ['inviteAccepted', {}, 'accepted your invitation'],
  ])('%s names what the actor did', (kind, overrides, expected) => {
    expect(notificationAction(entry({ kind, ...overrides }))).toBe(expected)
  })

  it('falls back to the message when no actor was recorded', () => {
    expect(notificationAction(entry({ actor: null }))).toBeNull()
  })

  it('reads a time relative to now', () => {
    const now = Date.parse('2026-10-21T12:00:00Z')
    expect(relativeTime('2026-10-20T18:00:00Z', now)).toBe('18 hours ago')
    expect(relativeTime('2026-10-21T11:59:40Z', now)).toBe('just now')
  })
})

describe('notification row', () => {
  it('shows actor, action, item and its current state, with the exact time on hover', () => {
    const row = mount(NotificationRow, {
      props: {
        entry: entry({ kind: 'transitioned', fromState: 'In Progress', toState: 'Review' }),
      },
    })
    expect(row.get('[data-testid="notification-headline"]').text()).toMatch(
      /^Ana Kovač moved a story from In Progress to Review · 18 hours ago$/,
    )
    expect(row.get('time').attributes('title')).toBe(new Date(entry().createdAt).toLocaleString())
    expect(row.get('[data-testid="notification-item"]').text()).toBe('Fix the login')
    expect(row.find('[aria-label="Story"]').exists()).toBe(true)
    expect(row.get('[data-testid="notification-meta"]').text()).toBe('WEB-7 • In Review')
  })

  it('joins an agent run headline without a space and badges the agent', () => {
    const row = mount(NotificationRow, {
      props: {
        entry: entry({
          kind: 'runSucceeded',
          actor: { id: 'a1', displayName: 'blagos-agent', avatarKey: null, isAgent: true },
        }),
      },
    })
    expect(row.get('[data-testid="notification-headline"]').text()).toContain(
      'blagos-agent’s run succeeded',
    )
    expect(row.find('[aria-label="blagos-agent is an agent"]').exists()).toBe(true)
  })

  it('renders an older row by its message, without item details it never had', () => {
    const row = mount(NotificationRow, {
      props: {
        entry: entry({
          actor: undefined,
          itemTitle: undefined,
          itemType: undefined,
          itemState: undefined,
        }),
      },
    })
    expect(row.get('[data-testid="notification-headline"]').text()).toContain(
      'A watched item has a new comment.',
    )
    expect(row.find('[data-testid="notification-item"]').exists()).toBe(false)
    expect(row.get('[data-testid="notification-meta"]').text()).toBe('WEB-7')
  })

  it('shows a sprint name on the item line', () => {
    const row = mount(NotificationRow, {
      props: {
        entry: entry({
          kind: 'sprintStarted',
          itemId: null,
          itemKey: null,
          itemType: null,
          itemState: null,
          itemTitle: 'Sprint 4',
        }),
      },
    })
    expect(row.get('[data-testid="notification-item"]').text()).toBe('Sprint 4')
    expect(row.get('[data-testid="notification-meta"]').text()).toBe('Sprint started')
  })
})
