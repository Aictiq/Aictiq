import type { ChatChannelType, NotificationKind, NotificationMode } from '@/api/notifications'
import { ApiError } from '@/utils/api'

/**
 * The words the app uses for notification kinds, channels and modes - one table, so the
 * inbox, personal settings and organization settings never name the same thing twice.
 *
 * Kept out of `@/api/notifications` on purpose: specs mock that module wholesale, and a
 * label table is not something a mock should have to repeat.
 */

/** Every kind, in the order the settings tables list them. */
export const notificationKinds: NotificationKind[] = [
  'assigned',
  'mentioned',
  'replied',
  'reacted',
  'commented',
  'transitioned',
  'claimed',
  'sprintStarted',
  'sprintCompleted',
  'wikiMentioned',
  'inviteAccepted',
  'runSucceeded',
  'runFailed',
  'runNeedsInput',
]

const kindLabels: Record<NotificationKind, string> = {
  assigned: 'Assigned',
  mentioned: 'Mentioned',
  replied: 'Replied',
  reacted: 'Reacted',
  commented: 'Commented',
  transitioned: 'Transitioned',
  claimed: 'Claimed',
  sprintStarted: 'Sprint started',
  sprintCompleted: 'Sprint completed',
  wikiMentioned: 'Wiki mention',
  inviteAccepted: 'Invite accepted',
  runSucceeded: 'Run succeeded',
  runFailed: 'Run failed',
  runNeedsInput: 'Run needs input',
}

/** A kind the API added after this build still reads as something rather than nothing. */
export const notificationKindLabel = (kind: string) => kindLabels[kind as NotificationKind] ?? kind

export const chatChannelTypes: ChatChannelType[] = ['telegram', 'slack', 'discord']

export const chatChannelLabels: Record<ChatChannelType, string> = {
  telegram: 'Telegram',
  slack: 'Slack',
  discord: 'Discord',
}

export const notificationModeLabels: Record<NotificationMode, string> = {
  off: 'Off',
  immediate: 'Immediate',
  digest: 'Daily digest',
}

/** What the API accepts as a webhook URL - said in the field, so a refusal is never a surprise. */
export type WebhookChannelType = Exclude<ChatChannelType, 'telegram'>
export const webhookUrlPrefixes: Record<WebhookChannelType, string> = {
  slack: 'https://hooks.slack.com/',
  discord: 'https://discord.com/api/webhooks/',
}

/**
 * Why a test message did not arrive. A 422 carries the destination's own words in `error`
 * (Slack's "invalid_token", Telegram's "bot was kicked"), which say more than any title.
 */
export function testFailureMessage(error: unknown) {
  if (error instanceof ApiError) {
    const reason = error.problem?.error
    return typeof reason === 'string' && reason ? reason : (error.problem?.detail ?? error.title)
  }
  return 'The test message could not be sent.'
}
