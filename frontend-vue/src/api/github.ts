import { apiFetch } from '@/utils/api'

export type GitHubInstallationStatus = 'active' | 'suspended' | 'deleted'

export interface GitHubInstallation {
  installationId: number
  accountLogin: string
  accountType: string
  status: GitHubInstallationStatus
  createdAt: string
  updatedAt: string
}

export interface GitHubRepository {
  id: number
  fullName: string
  installationId: number
}

export interface RepoBinding {
  id: string
  repoId: number
  installationId: number
  fullName: string
  onPullRequestOpenedStateId: string | null
  onPullRequestMergedStateId: string | null
  createdAt: string
}

export interface GitHubDelivery {
  deliveryId: string
  eventType: string
  action?: string | null
  status: string
  receivedAt: string
  processedAt?: string | null
  lastError?: string | null
}

const orgBase = (slug: string) => `/orgs/${slug}/github`
const projectBase = (slug: string, projectKey: string) =>
  `/orgs/${slug}/projects/${projectKey}/github/bindings`

export const githubInstallUrl = (slug: string) =>
  apiFetch<{ url: string }>(`${orgBase(slug)}/install-url`)
export const listGitHubInstallations = (slug: string) =>
  apiFetch<GitHubInstallation[]>(`${orgBase(slug)}/installations`)
export const disconnectGitHubInstallation = (slug: string, installationId: number) =>
  apiFetch<void>(`${orgBase(slug)}/installations/${installationId}`, { method: 'DELETE' })
export const listGitHubRepositories = (slug: string, installationId: number) =>
  apiFetch<GitHubRepository[]>(`${orgBase(slug)}/installations/${installationId}/repositories`)
export const listGitHubDeliveries = (slug: string) =>
  apiFetch<GitHubDelivery[]>(`${orgBase(slug)}/deliveries`)
export const reprocessGitHubDelivery = (slug: string, deliveryId: string) =>
  apiFetch<void>(`${orgBase(slug)}/deliveries/${deliveryId}/reprocess`, { method: 'POST' })

export const listGitHubBindings = (slug: string, projectKey: string) =>
  apiFetch<RepoBinding[]>(`${projectBase(slug, projectKey)}/`)
export const bindGitHubRepository = (slug: string, projectKey: string, repoId: number) =>
  apiFetch<RepoBinding>(`${projectBase(slug, projectKey)}/`, { method: 'POST', body: { repoId } })
export const unbindGitHubRepository = (slug: string, projectKey: string, repoId: number) =>
  apiFetch<void>(`${projectBase(slug, projectKey)}/${repoId}`, { method: 'DELETE' })
export const updateGitHubPullRequestRules = (
  slug: string,
  projectKey: string,
  repoId: number,
  rules: Pick<RepoBinding, 'onPullRequestOpenedStateId' | 'onPullRequestMergedStateId'>,
) =>
  apiFetch<RepoBinding>(`${projectBase(slug, projectKey)}/${repoId}/pull-request-rules`, {
    method: 'PUT',
    body: rules,
  })

export const createGitHubBranch = (slug: string, itemKey: string, repoId: number) =>
  apiFetch<void>(`/orgs/${slug}/items/${itemKey}/github/branch`, {
    method: 'POST',
    body: { repoId, from: 'default' },
  })
