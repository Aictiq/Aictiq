import { apiFetch } from '@/utils/api'
export interface AuditEntry { id: string; entityType: string; entityId: string; field: string; oldValue: string | null; newValue: string | null; userId: string | null; at: string }
export interface AuditPage { items: AuditEntry[]; page: number; hasMore: boolean }
export const getAudit = (slug: string, query: Record<string, string | number | undefined>) => apiFetch<AuditPage>(`/orgs/${slug}/audit`, { query })
export const auditExportUrl = (slug: string, query: Record<string, string | number | undefined>) => `/api/v1/orgs/${slug}/audit/export.csv?${new URLSearchParams(Object.entries(query).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)])).toString()}`
