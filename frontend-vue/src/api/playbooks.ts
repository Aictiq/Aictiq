import { apiFetch } from '@/utils/api'

/** Harness names are deliberately strings end to end: runners report the same values. */
export const playbookHarnesses = ['claude', 'codex', 'opencode'] as const
export type PlaybookHarness = (typeof playbookHarnesses)[number]

export interface Playbook {
  id: string
  projectId: string
  name: string
  /** Null after the backing page is deleted; the playbook stays visible so it can be repaired. */
  wikiPageId: string | null
  harness: PlaybookHarness
  onSuccessStateId: string | null
  onFailureStateId: string | null
  maxMinutes: number
  isDefault: boolean
  createdBy: string
  createdAt: string
  updatedAt: string
  /** xmin: every edit echoes the version that was read. */
  version: number
}

export interface SavePlaybookBody {
  name: string
  /**
   * What agents follow. Aictiq keeps it as a page in the wiki's Factory section, named after
   * the playbook, so its history is reviewable there.
   */
  instructionsMarkdown: string
  harness: PlaybookHarness
  onSuccessStateId: string | null
  onFailureStateId: string | null
  maxMinutes: number
}

export interface UpdatePlaybookBody extends Partial<SavePlaybookBody> {
  version: number
}

export interface PlaybookInstructions {
  wikiPageId: string | null
  pageTitle: string | null
  markdown: string
  /** False for a playbook whose page predates the Factory section; saving moves it there. */
  inFactorySection: boolean
}

export type RepositorySource = 0 | 1

export interface FactorySettings {
  projectId: string
  /** 0 uses a repository bound through the GitHub App; 1 is a path on the runner. */
  repoSource: RepositorySource
  repoFullName: string | null
  defaultBranch: string
  localPathHint: string | null
  defaultAgentId: string | null
  updatedAt: string | null
  version: number | null
}

export interface SaveFactorySettingsBody {
  repoSource: RepositorySource
  repoFullName: string | null
  defaultBranch: string
  localPathHint: string | null
  defaultAgentId: string | null
  version: number | null
}

const projectBase = (slug: string, projectKey: string) => `/orgs/${slug}/projects/${projectKey}`

const playbooksBase = (slug: string, projectKey: string) =>
  `${projectBase(slug, projectKey)}/playbooks`

export const listPlaybooks = (slug: string, projectKey: string) =>
  apiFetch<Playbook[]>(playbooksBase(slug, projectKey))

export const getPlaybook = (slug: string, projectKey: string, id: string) =>
  apiFetch<Playbook>(`${playbooksBase(slug, projectKey)}/${id}`)

export const createPlaybook = (slug: string, projectKey: string, body: SavePlaybookBody) =>
  apiFetch<Playbook>(playbooksBase(slug, projectKey), { method: 'POST', body })

export const getPlaybookInstructions = (slug: string, projectKey: string, id: string) =>
  apiFetch<PlaybookInstructions>(`${playbooksBase(slug, projectKey)}/${id}/instructions`)

export const updatePlaybook = (
  slug: string,
  projectKey: string,
  id: string,
  body: UpdatePlaybookBody,
) => apiFetch<Playbook>(`${playbooksBase(slug, projectKey)}/${id}`, { method: 'PATCH', body })

export const deletePlaybook = (slug: string, projectKey: string, id: string) =>
  apiFetch<void>(`${playbooksBase(slug, projectKey)}/${id}`, { method: 'DELETE' })

export const promotePlaybook = (slug: string, projectKey: string, id: string) =>
  apiFetch<Playbook>(`${playbooksBase(slug, projectKey)}/${id}/default`, { method: 'PUT' })

export const createStarterPlaybook = (slug: string, projectKey: string) =>
  apiFetch<Playbook>(`${playbooksBase(slug, projectKey)}/starter`, { method: 'POST' })

export const getFactorySettings = (slug: string, projectKey: string) =>
  apiFetch<FactorySettings>(`${projectBase(slug, projectKey)}/factory-settings`)

export const updateFactorySettings = (
  slug: string,
  projectKey: string,
  body: SaveFactorySettingsBody,
) =>
  apiFetch<FactorySettings>(`${projectBase(slug, projectKey)}/factory-settings`, {
    method: 'PUT',
    body,
  })
