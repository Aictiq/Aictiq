import { apiFetch } from '@/utils/api'

export type NotificationKind =
  | 'assigned'
  | 'mentioned'
  | 'commented'
  | 'transitioned'
  | 'claimed'
  | 'sprintStarted'
  | 'sprintCompleted'
  | 'wikiMentioned'
  | 'inviteAccepted'
  | 'replied'
  | 'reacted'
  | 'runSucceeded'
  | 'runFailed'
  | 'runNeedsInput'
export interface Notification {
  id: string
  organizationId: string
  runId?: string | null
  kind: NotificationKind
  projectId: string | null
  itemId: string | null
  itemKey: string | null
  message: string
  createdAt: string
  readAt: string | null
}
export type NotificationMode = 'off' | 'immediate' | 'digest'
/**
 * One kind's delivery. A chat channel's `null` means "the organization's default", which
 * in turn falls back to the email setting.
 */
export interface NotificationPreference {
  kind: NotificationKind
  inApp: boolean
  email: NotificationMode
  telegram: NotificationMode | null
  slack: NotificationMode | null
  discord: NotificationMode | null
}

export type ChatChannelType = 'telegram' | 'slack' | 'discord'
export type ChatChannelStatus = 'pending' | 'active' | 'broken'
export interface ChatChannel {
  id: string
  type: ChatChannelType
  status: ChatChannelStatus
  /** Masked by the API - enough to recognise the destination, never enough to post to it. */
  target: string | null
  lastError: string | null
  connectedAt: string | null
  createdAt: string
}
export interface OrgChatChannel extends ChatChannel {
  name: string
  modes: Partial<Record<NotificationKind, NotificationMode>>
}
/** A connect answer. The code and deep link are Telegram's alone: a webhook needs neither. */
export interface ChatChannelConnect<T extends ChatChannel = ChatChannel> {
  channel: T
  connectCode: string | null
  connectUrl: string | null
  expiresAt: string | null
}
export interface ChatChannelList {
  telegramAvailable: boolean
  telegramBotUsername: string | null
  channels: ChatChannel[]
}
export interface OrgChatChannelList {
  telegramAvailable: boolean
  telegramBotUsername: string | null
  /** The kinds an organization-wide channel can carry; personal kinds stay personal. */
  kinds: NotificationKind[]
  channels: OrgChatChannel[]
}
export interface OrgNotificationDefault {
  kind: NotificationKind
  email: NotificationMode | null
  telegram: NotificationMode | null
  slack: NotificationMode | null
  discord: NotificationMode | null
}

export const listNotifications = (unread = false) =>
  apiFetch<Notification[]>('/me/notifications', { query: { unread: unread || undefined } })
export const markNotificationsRead = (ids?: string[], all = false) =>
  apiFetch<{ read: number }>('/me/notifications/read', { method: 'POST', body: { ids, all } })
export const getNotificationPreferences = () =>
  apiFetch<NotificationPreference[]>('/me/notification-preferences')
export const putNotificationPreferences = (preferences: NotificationPreference[]) =>
  apiFetch<NotificationPreference[]>('/me/notification-preferences', {
    method: 'PUT',
    body: { preferences },
  })

export const listChatChannels = () => apiFetch<ChatChannelList>('/me/notification-channels')
export const connectChatChannel = (body: { type: ChatChannelType; webhookUrl?: string }) =>
  apiFetch<ChatChannelConnect>('/me/notification-channels', { method: 'POST', body })
/** 200 when the message went out; a 422 carries the destination's own `error`. */
export const testChatChannel = (id: string) =>
  apiFetch<{ ok: true }>(`/me/notification-channels/${id}/test`, { method: 'POST' })
export const disconnectChatChannel = (id: string) =>
  apiFetch<void>(`/me/notification-channels/${id}`, { method: 'DELETE' })

export const listOrgChatChannels = (slug: string) =>
  apiFetch<OrgChatChannelList>(`/orgs/${slug}/notification-channels`)
export const connectOrgChatChannel = (
  slug: string,
  body: { type: ChatChannelType; name: string; webhookUrl?: string },
) =>
  apiFetch<ChatChannelConnect<OrgChatChannel>>(`/orgs/${slug}/notification-channels`, {
    method: 'POST',
    body,
  })
export const updateOrgChatChannel = (
  slug: string,
  id: string,
  body: { name?: string; modes?: Partial<Record<NotificationKind, NotificationMode>> },
) =>
  apiFetch<OrgChatChannel>(`/orgs/${slug}/notification-channels/${id}`, { method: 'PATCH', body })
export const testOrgChatChannel = (slug: string, id: string) =>
  apiFetch<{ ok: true }>(`/orgs/${slug}/notification-channels/${id}/test`, { method: 'POST' })
export const disconnectOrgChatChannel = (slug: string, id: string) =>
  apiFetch<void>(`/orgs/${slug}/notification-channels/${id}`, { method: 'DELETE' })
export const getOrgNotificationDefaults = (slug: string) =>
  apiFetch<OrgNotificationDefault[]>(`/orgs/${slug}/notification-defaults`)
export const putOrgNotificationDefaults = (slug: string, defaults: OrgNotificationDefault[]) =>
  apiFetch<OrgNotificationDefault[]>(`/orgs/${slug}/notification-defaults`, {
    method: 'PUT',
    body: { defaults },
  })
