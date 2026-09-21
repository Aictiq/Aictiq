import { apiFetch } from '@/utils/api'

/**
 * Automation rules: "when an item enters *this state*, optionally carrying
 * *this label*, run *this playbook* as *this agent*." The workflow editor becomes the
 * assembly line — a rule is the conveyor between a station and a run.
 *
 * A rule fires from the same WorkItems integration event a manual "Hand to agent" click
 * would produce, through the one dispatch door: the same `item-claimed` refusal,
 * the same run row, just with no `requestedBy` — `ruleId`/`ruleName` say who asked instead.
 * Every firing (whether it dispatched or was skipped) is recorded, last 50 kept per rule.
 */

export interface RuleLastFiring {
  at: string
  itemKey: string
  runId: string | null
  skipReason: string | null
}

export interface Rule {
  id: string
  projectId: string
  projectKey: string
  projectName: string
  name: string
  triggerStateId: string
  /** Null means any label (or none) qualifies. */
  requiredLabelId: string | null
  playbookId: string
  playbookName: string
  agentId: string
  /** Null when the agent account was removed; the rule stays but cannot fire. */
  agentName: string | null
  enabled: boolean
  createdBy: string
  createdAt: string
  updatedAt: string
  /** xmin: echo it back on PATCH or the API answers 409. */
  version: number
  lastFiring: RuleLastFiring | null
}

export interface RuleFiring {
  itemId: string
  itemKey: string
  /** The idempotency key of the WorkItems event that triggered this firing. */
  eventId: string
  at: string
  /** Set only when the firing actually dispatched a run. */
  runId: string | null
  runStatus: string | null
  /** Set only when the firing was skipped rather than dispatched. */
  skipReason: string | null
}

export interface SaveRuleBody {
  name: string
  triggerStateId: string
  requiredLabelId?: string | null
  playbookId: string
  agentId: string
  enabled?: boolean
}

export interface UpdateRuleBody extends Partial<SaveRuleBody> {
  /** xmin, required on every PATCH. */
  version: number
}

/** Human text for a skip reason, falling back to the raw string for one this build does not know. */
const skipReasonText: Record<string, string> = {
  'item-claimed': 'Skipped: item already claimed',
  'run-in-progress': 'Skipped: a run is already in progress',
  'playbook-page-missing': "Skipped: the playbook's page is missing",
  'agent-unavailable': 'Skipped: the agent is unavailable',
  'item-not-found': 'Skipped: the item was not found',
  invalid: 'Skipped: invalid',
  'rule-loop': "Skipped: its own run moved the item here",
  'project-read-only': 'Skipped: the project is read-only',
}

export function ruleSkipReasonText(reason: string): string {
  return skipReasonText[reason] ?? `Skipped: ${reason}`
}

const orgRulesBase = (slug: string) => `/orgs/${slug}/rules`
const projectBase = (slug: string, projectKey: string) => `/orgs/${slug}/projects/${projectKey}`
const rulesBase = (slug: string, projectKey: string) => `${projectBase(slug, projectKey)}/rules`

/**
 * Every rule in every project the caller is project Admin of, across the organization —
 * what the Rules tab groups by project. A project where the caller is not Admin
 * contributes no rows, not a 403.
 */
export const listOrgRules = (slug: string) => apiFetch<Rule[]>(orgRulesBase(slug))

/** One project's rules. */
export const listRules = (slug: string, projectKey: string) =>
  apiFetch<Rule[]>(rulesBase(slug, projectKey))

export const getRule = (slug: string, projectKey: string, id: string) =>
  apiFetch<Rule>(`${rulesBase(slug, projectKey)}/${id}`)

export const createRule = (slug: string, projectKey: string, body: SaveRuleBody) =>
  apiFetch<Rule>(rulesBase(slug, projectKey), { method: 'POST', body })

export const updateRule = (
  slug: string,
  projectKey: string,
  id: string,
  body: UpdateRuleBody,
) => apiFetch<Rule>(`${rulesBase(slug, projectKey)}/${id}`, { method: 'PATCH', body })

export const deleteRule = (slug: string, projectKey: string, id: string) =>
  apiFetch<void>(`${rulesBase(slug, projectKey)}/${id}`, { method: 'DELETE' })

/** Last 50 firings of one rule, newest first. */
export const listRuleFirings = (slug: string, projectKey: string, id: string) =>
  apiFetch<RuleFiring[]>(`${rulesBase(slug, projectKey)}/${id}/firings`)
