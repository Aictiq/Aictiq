import { apiFetch } from '@/utils/api'

export interface BurndownDay {
  day: string
  scope: number
  remaining: number
  completed: number
  idealRemaining: number
  scopeChanges: { itemId: string; added: boolean; value: number; at: string }[]
}
export interface SprintHealth {
  sprintId: string
  percentDone: number
  daysElapsed: number
  workingDays: number
  projectedCompletionOn: string | null
  blockedCount: number
  unestimatedCount: number
  unassignedCount: number
  agentSharePercent: number
}
export interface CumulativeFlowDay { day: string; counts: Record<string, number> }
export interface CycleSummary { p50: number; p85: number; p95: number }
export interface CyclePoint { itemKey: string; leadDays: number; cycleDays: number; assigneeId: string | null; type: string }
export interface ThroughputPoint { weekOf: string; count: number; agentCount: number; humanCount: number }
export interface CycleTime { leadTime: CycleSummary; cycleTime: CycleSummary; items: CyclePoint[]; throughput: ThroughputPoint[] }
export interface DashboardWidget { id: string; type: string; x: number; y: number; w: number; h: number; config: Record<string, unknown> }
export interface Dashboard { id: string; name: string; ownerUserId: string | null; isDefault: boolean; layout: DashboardWidget[]; version: number }
export interface DashboardData { id: string; widgets: { id: string; type: string; data: unknown; error: string | null }[] }
export interface OrganizationOverview { openBugs: number; blockedItems: number; agentActionsLast24Hours: number; projects: { id: string; key: string; name: string; openBugs: number; blockedItems: number; agentActionsLast24Hours: number; activeSprints: { id: string; name: string; percentDone: number; blockedCount: number }[] }[]; recentActivity: { itemKey: string; itemTitle: string; field: string; at: string; actor: { id: string; displayName: string } | null }[]; agentsAtWork: { id: string; displayName: string }[] }

const projectBase = (slug: string, key: string) => `/orgs/${slug}/projects/${key}`
export const getBurndown = (slug: string, sprintId: string, unit: 'points' | 'hours') => apiFetch<{ sprintId: string; unit: string; days: BurndownDay[] }>(`/orgs/${slug}/sprints/${sprintId}/burndown`, { query: { unit } })
export const getSprintHealth = (slug: string, sprintId: string) => apiFetch<SprintHealth>(`/orgs/${slug}/sprints/${sprintId}/health`)
export const getFlow = (slug: string, key: string, query: Record<string, string>) => apiFetch<{ days: CumulativeFlowDay[] }>(`${projectBase(slug, key)}/flow`, { query })
export const getCycleTime = (slug: string, key: string, query: Record<string, string>) => apiFetch<CycleTime>(`${projectBase(slug, key)}/cycle-time`, { query })
export const listDashboards = (slug: string, key: string) => apiFetch<Dashboard[]>(`${projectBase(slug, key)}/dashboards`)
export const createDashboard = (slug: string, key: string, body: { name: string; layout: DashboardWidget[]; shared?: boolean }) => apiFetch<Dashboard>(`${projectBase(slug, key)}/dashboards`, { method: 'POST', body })
export const updateDashboard = (slug: string, key: string, id: string, body: { name?: string; layout?: DashboardWidget[]; shared?: boolean; version: number }) => apiFetch<Dashboard>(`${projectBase(slug, key)}/dashboards/${id}`, { method: 'PATCH', body })
export const deleteDashboard = (slug: string, key: string, id: string) => apiFetch<void>(`${projectBase(slug, key)}/dashboards/${id}`, { method: 'DELETE' })
export const getDashboardData = (slug: string, key: string, id: string) => apiFetch<DashboardData>(`${projectBase(slug, key)}/dashboards/${id}/data`)
export const getOrganizationOverview = (slug: string) => apiFetch<OrganizationOverview>(`/orgs/${slug}/overview`)
