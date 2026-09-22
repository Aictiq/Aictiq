import { apiFetch } from '@/utils/api'

/**
 * What the agents in an organization are doing.
 *
 * Both endpoints are bounded by the caller's visible projects before anything is paged, so
 * a Guest sees an organization's agents only through the projects they could already read.
 */

export interface ActivityActor {
  id: string
  displayName: string
  avatarKey: string | null
  isAgent: boolean
}

export interface AgentActivityEntry {
  /** `changed` for a history event, `commented` for a comment. */
  kind: 'changed' | 'commented'
  itemKey: string
  itemTitle: string
  actor: ActivityActor | null
  at: string
  /** Already rendered for a feed: the fields that changed, or the comment's first line. */
  summary: string
}

export interface AgentContributionRow {
  agent: ActivityActor
  completed: number
  inProgress: number
}

export interface AgentContribution {
  completedByAgents: number
  completedTotal: number
  byAgent: AgentContributionRow[]
}

export const listAgentActivity = (
  slug: string,
  options: { actorId?: string; agentsOnly?: boolean; limit?: number } = {},
) =>
  apiFetch<AgentActivityEntry[]>(`/orgs/${slug}/agent-activity/`, {
    query: { actorId: options.actorId, agentsOnly: options.agentsOnly, limit: options.limit },
  })

/** Agent share of the finished work in the currently active sprints. */
export const getAgentContribution = (slug: string) =>
  apiFetch<AgentContribution>(`/orgs/${slug}/agent-activity/contribution`)
