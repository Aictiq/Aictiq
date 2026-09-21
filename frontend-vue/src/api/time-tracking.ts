import { apiFetch } from '@/utils/api'

/** The time-related subset returned with every work item. */
export interface TimeTrackingItem {
  key: string
  estimateHours: number | null
  remainingHours: number | null
  completedHours: number | null
  version: number
}

/** Records completed work and returns the item's updated hours and concurrency version. */
export function logTime(slug: string, itemKey: string, hours: number, version: number) {
  return apiFetch<TimeTrackingItem>(`/orgs/${slug}/items/${itemKey}/log-time`, {
    method: 'POST',
    body: { hours, version },
  })
}
