import { apiFetch } from '@/utils/api'

export type WebhookEvent = 'item.created' | 'item.updated' | 'item.transitioned' | 'item.commented' | 'sprint.started' | 'sprint.completed' | 'wiki.page.updated' | 'agent.claimed'
export interface Webhook { id: string; projectId: string | null; url: string; events: WebhookEvent[]; active: boolean; consecutiveFailures: number; createdAt: string; updatedAt: string }
export interface WebhookCreated { subscription: Webhook; secret: string }
export interface WebhookDelivery { id: string; eventId: string; event: string; attempt: number; status: string; statusCode: number | null; responseExcerpt: string | null; lastError: string | null; createdAt: string; deliveredAt: string | null; nextAttemptAt: string }

export const listWebhooks = (slug: string) => apiFetch<Webhook[]>(`/orgs/${slug}/webhooks`)
export const createWebhook = (slug: string, body: { url: string; events: WebhookEvent[]; projectId?: string }) => apiFetch<WebhookCreated>(`/orgs/${slug}/webhooks`, { method: 'POST', body })
export const deleteWebhook = (slug: string, id: string) => apiFetch<void>(`/orgs/${slug}/webhooks/${id}`, { method: 'DELETE' })
export const testWebhook = (slug: string, id: string) => apiFetch<WebhookDelivery>(`/orgs/${slug}/webhooks/${id}/test`, { method: 'POST' })
export const listWebhookDeliveries = (slug: string, id: string) => apiFetch<WebhookDelivery[]>(`/orgs/${slug}/webhooks/${id}/deliveries`)
export const redeliverWebhook = (slug: string, id: string, deliveryId: string) => apiFetch<WebhookDelivery>(`/orgs/${slug}/webhooks/${id}/deliveries/${deliveryId}/redeliver`, { method: 'POST' })
