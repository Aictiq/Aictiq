import { apiFetch } from '@/utils/api'

export type NotificationKind = string
export interface Notification { id: string; kind: NotificationKind; projectId: string | null; itemId: string | null; itemKey: string | null; message: string; createdAt: string; readAt: string | null }
export type NotificationEmailMode = 'off' | 'immediate' | 'digest'
export interface NotificationPreference { kind: NotificationKind; inApp: boolean; email: NotificationEmailMode }
export const listNotifications = (unread = false) => apiFetch<Notification[]>('/me/notifications', { query: { unread: unread || undefined } })
export const markNotificationsRead = (ids?: string[], all = false) => apiFetch<{ read: number }>('/me/notifications/read', { method: 'POST', body: { ids, all } })
export const getNotificationPreferences = () => apiFetch<NotificationPreference[]>('/me/notification-preferences')
export const putNotificationPreferences = (preferences: NotificationPreference[]) => apiFetch<NotificationPreference[]>('/me/notification-preferences', { method: 'PUT', body: { preferences } })
