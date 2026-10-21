import type { WorkItemType } from '@/api/items'
import type {
  ChatChannelType,
  Notification,
  NotificationKind,
  NotificationMode,
} from '@/api/notifications'
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
  'runRefined',
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
  runRefined: 'Refinement ready',
}

/** A kind the API added after this build still reads as something rather than nothing. */
export const notificationKindLabel = (kind: string) => kindLabels[kind as NotificationKind] ?? kind

const articles: Record<WorkItemType, string> = {
  epic: 'an epic',
  feature: 'a feature',
  story: 'a story',
  task: 'a task',
  bug: 'a bug',
}

/**
 * What a notification says happened, after the actor's name: "commented on a story". Null
 * when the row does not name an actor - an older row, or an agent the reader may not see -
 * so the caller shows the row's own message instead.
 */
export function notificationAction(entry: Notification): string | null {
  if (!entry.actor) return null
  const item = (entry.itemType && articles[entry.itemType]) ?? 'an item'
  switch (entry.kind) {
    case 'assigned':
      return `assigned ${item} to you`
    case 'mentioned':
      return 'mentioned you'
    case 'commented':
      return `commented on ${item}`
    case 'replied':
      return 'replied to your comment'
    case 'reacted':
      return entry.emoji ? `reacted ${entry.emoji} to your comment` : 'reacted to your comment'
    case 'transitioned':
      if (entry.fromState && entry.toState)
        return `moved ${item} from ${entry.fromState} to ${entry.toState}`
      return entry.toState ? `moved ${item} to ${entry.toState}` : `moved ${item}`
    case 'claimed':
      return `claimed ${item}`
    case 'sprintStarted':
      return 'started a sprint'
    case 'sprintCompleted':
      return 'completed a sprint'
    case 'wikiMentioned':
      return 'mentioned you on a wiki page'
    case 'inviteAccepted':
      return 'accepted your invitation'
    case 'runSucceeded':
      return '’s run succeeded'
    case 'runNeedsInput':
      return '’s refine run needs your input'
    case 'runRefined':
      return `refined ${item}, ready for review`
    case 'runFailed':
      // The outcome is only in the stored sentence; say the same thing it does.
      if (entry.message.includes('timed out')) return '’s run timed out'
      if (entry.message.includes('cancelled')) return '’s run was cancelled'
      if (entry.message.includes('without a refined ticket'))
        return '’s refine run finished without a refined ticket'
      return '’s run failed'
    default:
      return null
  }
}

const relativeUnits: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
]

/** "18 hours ago", "yesterday", "just now". */
export function relativeTime(value: string, now = Date.now()) {
  const seconds = Math.round((new Date(value).getTime() - now) / 1000)
  const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  for (const [unit, size] of relativeUnits)
    if (Math.abs(seconds) >= size) return format.format(Math.round(seconds / size), unit)
  return 'just now'
}

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
