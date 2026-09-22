import type { components } from './schema.js'

/**
 * Response shapes.
 *
 * The API's minimal-API endpoints return untyped `IResult`, so the OpenAPI document
 * describes paths, parameters and *request* bodies but not responses - those are declared
 * here by hand, mirroring the `*View` records in `backend/src/Modules/**\/Endpoints`.
 * Request bodies and route names still come from the generated schema, so a renamed field
 * or a dropped route is a type error. When the document gains response schemas,
 * it lands, these should be replaced with `components["schemas"][...]`.
 */
export type WorkItemType = components['schemas']['WorkItemType']
export type WorkItemPriority = NonNullable<components['schemas']['WorkItemPriority']>
export type WorkflowStateCategory = components['schemas']['WorkflowStateCategory']
export type OrgRole = NonNullable<components['schemas']['OrgRole']>
export type ProjectRole = NonNullable<components['schemas']['ProjectRole']>
export type ProjectVisibility = NonNullable<components['schemas']['ProjectVisibility']>

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface OrganizationSummary {
  id: string
  slug: string
  name: string
  role: OrgRole
}

export interface SessionResponse {
  id: string
  email: string
  firstName: string | null
  lastName: string | null
  roles: string[]
}

export interface ProjectView {
  id: string
  key: string
  name: string
  description: string | null
  visibility: ProjectVisibility
  icon: string | null
  color: string | null
  isArchived: boolean
  createdAt: string
  role: ProjectRole
  version: number
}

export interface ItemLabelView {
  id: string
  name: string
  color: string | null
  group: string | null
}

export interface ItemRollup {
  totalCount: number
  completedCount: number
  pointsTotal: number
  pointsCompleted: number
  remainingHours: number
}

export interface WorkItemView {
  id: string
  key: string
  type: WorkItemType
  title: string
  descriptionMarkdown: string
  descriptionHtml: string
  stateId: string
  stateCategory: WorkflowStateCategory
  priority: WorkItemPriority
  assigneeId: string | null
  teamId: string | null
  sprintId: string | null
  parentId: string | null
  points: number | null
  estimateHours: number | null
  remainingHours: number | null
  completedHours: number | null
  dueDate: string | null
  createdAt: string
  updatedAt: string
  version: number
  rollup: ItemRollup
  labels: ItemLabelView[]
  blocked: boolean
  isWatching: boolean
  watcherCount: number
  claimedBy: string | null
  claimedAt: string | null
  claimHeartbeatAt: string | null
}

export interface WorkflowStateView {
  id: string
  name: string
  category: WorkflowStateCategory
  position: number
  color: string | null
  isInitial: boolean
}

export interface WorkflowView {
  id: string
  name: string
  isDefault: boolean
  version: number
  states: WorkflowStateView[]
  transitions: { fromStateId: string | null; toStateId: string }[]
}

export interface CommentAuthorView {
  id: string
  displayName: string
  avatarKey: string | null
  isAgent: boolean
}

export interface CommentView {
  id: string
  author: CommentAuthorView
  bodyMarkdown: string
  bodyHtml: string
  createdAt: string
  editedAt: string | null
  deletedAt: string | null
  mentions: string[]
  reactions: { emoji: string; count: number; reactedByMe: boolean }[]
}

export interface ItemLinkView {
  id: string
  kind: string
  provider: string
  externalId: string
  url: string
  title: string | null
  state: string | null
  createdAt: string
  branch: string | null
}

export interface TeamView {
  id: string
  name: string
  key: string
  sprintLengthDays: number
  isDefault: boolean
  memberCount: number
  isLead: boolean
  version: number
}

export interface SprintProgress {
  totalItems: number
  completedItems: number
  pointsTotal: number
  pointsDone: number
  remainingHours: number
}

export interface SprintView {
  id: string
  teamId: string
  name: string
  goal: string
  startsOn: string
  endsOn: string
  state: 'planned' | 'active' | 'completed'
  autoCreateNext: boolean
  daysLeft: number
  progress: SprintProgress
  version: number
}

export interface WikiTreePageView {
  id: string
  slug: string
  title: string
  parentId: string | null
  position: number
  updatedAt: string
}

export interface WikiPageView {
  id: string
  projectId: string
  slug: string
  title: string
  parentId: string | null
  position: number
  contentMarkdown: string
  contentHtml: string
  revisionNumber: number
  summary: string | null
  updatedAt: string
  version: number
}

export type RunStatus =
  'queued' | 'assigned' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'timedOut'

/** A factory run (Automation's `RunView`); the detail fields are null for non-operators. */
export interface RunView {
  id: string
  projectId: string
  itemId: string
  itemKey: string
  playbookId: string
  playbookName: string | null
  agentId: string
  agentName: string | null
  /** Null when an automation rule dispatched the run instead of a person; see `ruleId`/`ruleName`. */
  requestedBy: string | null
  ruleId: string | null
  ruleName: string | null
  runnerId: string | null
  runnerName: string | null
  status: RunStatus
  harness: string
  playbookRevisionId: string | null
  maxMinutes: number
  queuedAt: string
  assignedAt: string | null
  startedAt: string | null
  finishedAt: string | null
  lastHeartbeatAt: string | null
  cancelRequested: boolean
  outcomeSummary: string | null
  pullRequestUrl: string | null
  exitCode: number | null
  costUsd: number | null
  inputTokens: number | null
  outputTokens: number | null
  failureReason: string | null
  promptSnapshot: string | null
  version: number
}

export interface RunLogEntry {
  seq: number
  at: string
  stream: 'stdout' | 'stderr' | 'event'
  text: string
}

export interface RunLogPage {
  items: RunLogEntry[]
  truncated: boolean
}

export interface PlaybookView {
  id: string
  projectId: string
  name: string
  wikiPageId: string | null
  harness: string
  onSuccessStateId: string | null
  onFailureStateId: string | null
  maxMinutes: number
  isDefault: boolean
  createdBy: string
  createdAt: string
  updatedAt: string
  version: number
}

export interface AgentView {
  userId: string
  displayName: string
  email: string
  ownerUserId: string
  ownerName: string
  role: OrgRole
  isActive: boolean
  createdAt: string
  lastActiveAt: string | null
  tokenCount: number
}
