import type { StateCategory } from '@/components/common/StateBadge.vue'
import { apiFetch } from '@/utils/api'

/** A project's configurable lifecycle. The editor is not built yet; this contract is
 * deliberately complete now so read-only consumers do not need a second endpoint later. */
export interface WorkflowState {
  id: string
  name: string
  category: StateCategory
  position: number
  color: string | null
  isInitial: boolean
}

export interface WorkflowTransition {
  fromStateId: string | null
  toStateId: string
}

export interface Workflow {
  id: string
  name: string
  isDefault: boolean
  version: number
  states: WorkflowState[]
  transitions: WorkflowTransition[]
}

/** Workflows are project-scoped, so the organization and permanent project key stay in
 * the path just like every other project settings request. */
export const listWorkflows = (slug: string, projectKey: string) =>
  apiFetch<Workflow[]>(`/orgs/${slug}/projects/${projectKey}/workflows`)
