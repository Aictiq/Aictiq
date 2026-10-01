import { apiFetch } from '@/utils/api'

/**
 * Ticket refinement: a person files a short description and a refine run - an ordinary
 * factory run with the project's refine playbook - rewrites the item into a complete ticket,
 * or asks the questions it cannot answer itself. The person reviews, answers or edits, and
 * confirms; confirming moves the item to the project's refined state.
 */

export type RefinementStatus = 'refining' | 'needsInput' | 'ready' | 'failed' | 'confirmed'

export interface RefinementSettings {
  projectId: string
  /** The playbook refine runs follow; null turns refinement off. */
  playbookId: string | null
  /** The agent refine runs execute as; null uses the project's default agent. */
  agentId: string | null
  productDescription: string
  writingInstructions: string
  namingConventions: string
  platforms: string
  /** The workflow state a confirmed ticket moves to; null leaves it where it was filed. */
  refinedStateId: string | null
  enabled: boolean
  updatedAt: string | null
  version: number
}

export type SaveRefinementSettingsBody = Omit<
  RefinementSettings,
  'projectId' | 'enabled' | 'updatedAt'
>

export interface RefinementAnswer {
  question: string
  answer: string
}

export interface Refinement {
  id: string
  itemId: string
  itemKey: string
  status: RefinementStatus
  /** Open questions from the last run, when it needs input. */
  questions: string[]
  /** Everything answered so far, oldest first. */
  answered: RefinementAnswer[]
  summary: string | null
  lastRunId: string | null
  requestedBy: string
  createdAt: string
  updatedAt: string
  confirmedAt: string | null
  confirmedBy: string | null
  refinedStateId: string | null
  version: number
}

export interface RefineBody {
  answers?: RefinementAnswer[]
  /** What should change, when asking again without having been asked. */
  feedback?: string
  runnerId?: string | null
}

const settingsPath = (slug: string, projectKey: string) =>
  `/orgs/${slug}/projects/${projectKey}/refinement-settings`
const itemPath = (slug: string, itemKey: string) => `/orgs/${slug}/items/${itemKey}/refinement`

export const getRefinementSettings = (slug: string, projectKey: string) =>
  apiFetch<RefinementSettings>(`${settingsPath(slug, projectKey)}/`)

export const saveRefinementSettings = (
  slug: string,
  projectKey: string,
  body: SaveRefinementSettingsBody,
) => apiFetch<RefinementSettings>(`${settingsPath(slug, projectKey)}/`, { method: 'PUT', body })

/** Creates a "Refine" playbook with starter instructions and chooses it for refinement. */
export const createRefinePlaybook = (slug: string, projectKey: string) =>
  apiFetch<RefinementSettings>(`${settingsPath(slug, projectKey)}/starter-playbook`, {
    method: 'POST',
  })

/** The item's refinement, or null when it was never refined. */
export const getRefinement = async (slug: string, itemKey: string) =>
  (await apiFetch<Refinement | undefined>(`${itemPath(slug, itemKey)}/`)) ?? null

export const refineItem = (slug: string, itemKey: string, body: RefineBody = {}) =>
  apiFetch<Refinement>(`${itemPath(slug, itemKey)}/`, { method: 'POST', body })

export const confirmRefinement = (slug: string, itemKey: string, version: number) =>
  apiFetch<Refinement>(`${itemPath(slug, itemKey)}/confirm`, {
    method: 'POST',
    body: { version },
  })
