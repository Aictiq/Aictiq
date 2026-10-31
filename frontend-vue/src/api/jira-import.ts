import type { StateCategory } from '@/components/common/StateBadge.vue'
import type { WorkItemType } from '@/api/items'
import { apiFetch } from '@/utils/api'

/**
 * A one-time migration of one Jira Cloud project into this one. The credentials travel
 * with every call and are never stored by the SPA: the API checks them, reads Jira with
 * them, and forgets them once the import job ends.
 */
export interface JiraCredentials {
  siteUrl: string
  email: string
  apiToken: string
}

export interface JiraProjectRef {
  id: string
  key: string
  name: string
}

export interface JiraConnection {
  /** Normalised by the API, e.g. `https://acme.atlassian.net`. */
  siteUrl: string
  accountName: string
  projects: JiraProjectRef[]
}

export type JiraStatusCategory = 'new' | 'indeterminate' | 'done' | 'unknown'

export interface JiraImportPreview {
  jiraProject: JiraProjectRef
  issueCount: number
  /** Issues whose key is already on an item here; the import skips them. */
  alreadyImported: number
  issueTypes: { name: string; suggestedType: WorkItemType }[]
  statuses: { name: string; category: JiraStatusCategory; suggestedStateId: string }[]
  users: {
    accountId: string
    displayName: string
    matchedUserId: string | null
    matchedDisplayName: string | null
  }[]
  states: { id: string; name: string; category: StateCategory }[]
}

export type JiraImportStatus = 'pending' | 'running' | 'completed' | 'failed'
export type JiraImportFailureKind = 'issue' | 'comment' | 'attachment' | 'sprint' | 'parent' | 'job'

export interface JiraImportFailure {
  issueKey: string | null
  kind: JiraImportFailureKind
  reason: string
}

export interface JiraImportJob {
  id: string
  jiraProjectKey: string
  jiraProjectName: string
  siteUrl: string
  status: JiraImportStatus
  totalIssues: number
  processedIssues: number
  createdItems: number
  skippedIssues: number
  createdComments: number
  createdAttachments: number
  createdSprints: number
  failures: JiraImportFailure[]
  createdAt: string
  startedAt: string | null
  completedAt: string | null
}

export interface StartJiraImport extends JiraCredentials {
  jiraProjectKey: string
  issueTypes: Record<string, WorkItemType>
  /** Jira status name to workflow state id. */
  statuses: Record<string, string>
}

const base = (slug: string, projectKey: string) => `/orgs/${slug}/projects/${projectKey}`

export const connectJira = (slug: string, projectKey: string, body: JiraCredentials) =>
  apiFetch<JiraConnection>(`${base(slug, projectKey)}/import/jira/connect`, {
    method: 'POST',
    body,
  })

export const previewJiraImport = (
  slug: string,
  projectKey: string,
  body: JiraCredentials & { jiraProjectKey: string },
) =>
  apiFetch<JiraImportPreview>(`${base(slug, projectKey)}/import/jira/preview`, {
    method: 'POST',
    body,
  })

export const startJiraImport = (slug: string, projectKey: string, body: StartJiraImport) =>
  apiFetch<JiraImportJob>(`${base(slug, projectKey)}/import/jira`, { method: 'POST', body })

export const listJiraImports = (slug: string, projectKey: string) =>
  apiFetch<JiraImportJob[]>(`${base(slug, projectKey)}/imports/jira`)

export const getJiraImport = (slug: string, projectKey: string, jobId: string) =>
  apiFetch<JiraImportJob>(`${base(slug, projectKey)}/imports/jira/${jobId}`)

/** Still moving: the result screen keeps polling until this is false. */
export const isJiraImportActive = (job: JiraImportJob) =>
  job.status === 'pending' || job.status === 'running'
