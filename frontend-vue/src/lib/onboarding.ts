import type { RouteLocationRaw } from 'vue-router'

import type { TourStepId } from '@/api/onboarding'
import type { OrgRole } from '@/api/organizations'
import { factoryPath, projectItemsPath, wikiPath } from '@/router/paths'

/**
 * The product tour as data, and the Get started checklist's per-project memory.
 *
 * Steps are declared here rather than in the component so the copy and the routing stay
 * reviewable in one place, and so the store can persist "stopped on step X" without
 * knowing what any step says. Nothing here imports a store or the router: everything a
 * step needs arrives in `TourContext`, and routes come back as plain objects.
 *
 * Copy rules: one idea per step, plain English, role-aware — a stakeholder is told what
 * the factory *is*, never asked to operate it.
 */

/** What a step may need to know about the person and where they are. */
export interface TourContext {
  hasOrganization: boolean
  canOperateFactory: boolean
  orgRole: OrgRole | undefined
  /** The selected organization's slug, for building a step's route. */
  orgSlug: string | null
  projectKey: string | null
  hasProject: boolean
  hasTeam: boolean
}

export interface TourCta {
  label: string
  /** `open-checklist` opens Get started; `pause` just ends the tour for now. */
  run: 'open-checklist' | 'pause'
}

export interface TourStep {
  id: TourStepId
  title: string
  body: (ctx: TourContext) => string
  /** data-tour anchor attribute value; null → centered fallback card. */
  anchor: (ctx: TourContext) => string | null
  /** Route to open when the step activates; null stays put. */
  route?: (ctx: TourContext) => RouteLocationRaw | null
  /** Optional call-to-action button on the callout. */
  cta?: (ctx: TourContext) => TourCta | null
  /** Eligibility; false skips the step entirely. Default true. */
  when?: (ctx: TourContext) => boolean
}

export const TOUR_STEPS: TourStep[] = [
  {
    id: 'navigation',
    title: 'Find your way around',
    body: (ctx) =>
      ctx.hasProject
        ? 'My work and Inbox are yours alone. The other sections follow what you have selected: items and wiki for the project, backlog, board and sprints for the team. Search opens with Ctrl/Cmd+K, and ? shows every shortcut.'
        : 'My work and Inbox are yours alone. Sections appear as you select a project and a team, and everything follows the organization you are in. Search opens with Ctrl/Cmd+K, and ? shows every shortcut.',
    anchor: () => 'sidebar-nav',
  },
  {
    id: 'organization',
    title: 'Organizations hold everything',
    body: (ctx) =>
      ctx.hasOrganization
        ? 'This organization holds your members, projects, agents and runners. Switching it with the control above changes everything you can see and operate.'
        : 'Create an organization to begin — it will hold your members, projects, agents and runners. The control above is where you switch between them.',
    anchor: () => 'org-switcher',
  },
  {
    id: 'project-team',
    title: 'Pick a project, then a team',
    body: () =>
      'Choosing a project selects its items and wiki. Choosing a team gives that team a backlog, a board and sprints.',
    anchor: (ctx) => (ctx.hasProject ? 'project-list' : null),
  },
  {
    id: 'board',
    title: 'The board is real work',
    body: (ctx) =>
      ctx.hasProject && ctx.hasTeam
        ? 'Columns are workflow states and cards are items. Opening a card shows its details, and moving one is a real change — it moves the item for everyone. Filters can hide work from the view without touching it.'
        : 'Once a project and team are selected, their board appears here: columns of workflow states you move cards across. Moving a card is a real change, not a preview.',
    anchor: () => 'board-filter',
    route: (ctx) => (ctx.hasProject && ctx.hasTeam ? { path: '/board' } : null),
  },
  {
    id: 'prepare-item',
    title: 'Write a ticket an agent can run',
    body: () =>
      'A good ticket states the outcome, the context and steps to try, and how to tell it is done — attached files travel with the item. Assigning an agent does not start work; that takes an explicit handoff.',
    anchor: (ctx) => (ctx.hasProject ? 'items-link' : null),
    route: (ctx) =>
      ctx.orgSlug && ctx.projectKey ? { path: projectItemsPath(ctx.orgSlug, ctx.projectKey) } : null,
    cta: (ctx) => (ctx.hasProject ? { label: 'Create a ticket', run: 'pause' } : null),
  },
  {
    id: 'project-knowledge',
    title: 'Keep project knowledge in the wiki',
    body: () =>
      'Repository conventions, test commands and how to run the project belong in the wiki. Playbooks point at wiki pages, so what an agent reads stays yours to write.',
    anchor: (ctx) => (ctx.hasProject ? 'wiki-link' : null),
    route: (ctx) =>
      ctx.orgSlug && ctx.projectKey ? { path: wikiPath(ctx.orgSlug, ctx.projectKey) } : null,
  },
  {
    id: 'factory-concepts',
    title: 'How the factory works',
    body: (ctx) =>
      ctx.canOperateFactory
        ? 'Four words cover it: an agent is an accountable bot identity, a runner is the machine that executes it, a playbook is the reusable instructions, and a run is one attempt on one item.'
        : 'AI work here is started by an authorized teammate: they hand an item to an agent, and the outcome is reported back on the item.',
    anchor: (ctx) => (ctx.canOperateFactory ? 'factory-link' : null),
    route: (ctx) =>
      ctx.canOperateFactory && ctx.orgSlug ? { path: factoryPath(ctx.orgSlug, 'runs') } : null,
  },
  {
    id: 'runner-setup',
    title: 'Bring a runner online',
    body: () =>
      'A runner is a machine that executes runs: prepare the machine, register the runner, and wait for it to show as online. A shared organization runner may already exist. Longer instructions live in Get started.',
    anchor: () => 'factory-runners',
    route: (ctx) => (ctx.orgSlug ? { path: factoryPath(ctx.orgSlug, 'runners') } : null),
    when: (ctx) => ctx.canOperateFactory,
  },
  {
    id: 'handoff',
    title: 'Hand an item to an agent',
    body: () =>
      'On the item, the Hand to agent button picks an agent and a playbook and starts a run. Nothing runs until that click — the handoff is always explicit.',
    anchor: () => null,
    cta: () => ({ label: 'Prepare first handoff', run: 'open-checklist' }),
    when: (ctx) => ctx.canOperateFactory,
  },
  {
    id: 'follow-up',
    title: 'Follow up on the result',
    body: (ctx) =>
      ctx.canOperateFactory
        ? 'Run status and outcome comments land on the item and in your inbox. A finished run is not proof of good code — review the diff. As an operator you can read the log and cancel a run.'
        : 'Run status and outcome comments land on the item and in your inbox. A finished run is not proof of good code — review the result before calling it done.',
    anchor: () => 'inbox-link',
  },
]

/** The steps this person is offered, in order — operators get two more than stakeholders. */
export function tourSteps(ctx: TourContext): TourStep[] {
  return TOUR_STEPS.filter((step) => (step.when ? step.when(ctx) : true))
}

// ── The Get started checklist's per-project memory ──────────────────────────────────

const CHECKLIST_PREFIX = 'aictiq.gs.'

export type ChecklistTaskStatus = 'ready' | 'needs-setup' | 'needs-admin' | 'unavailable'

export interface ChecklistHint {
  /** The item key (e.g. `PROJ-12`) the person prepared for handoff, if any. */
  itemId?: string | null
  /** The person has looked at the finished run's result. */
  reviewed?: boolean
}

function checklistKey(userId: string, slug: string, projectKey: string): string {
  return `${CHECKLIST_PREFIX}${userId}.${slug}.${projectKey}`
}

const emptyHint = (): ChecklistHint => ({ itemId: null, reviewed: false })

/**
 * Per-browser, per-project progress. Storage is a convenience, never a permission — a
 * missing, unreadable or unparseable slot reads as the same empty hint as a fresh one, so
 * callers never have to tell "nothing saved" from "storage is blocked".
 */
export function readChecklistHint(userId: string, slug: string, projectKey: string): ChecklistHint {
  try {
    const raw = localStorage.getItem(checklistKey(userId, slug, projectKey))
    if (!raw) return emptyHint()
    const parsed = JSON.parse(raw) as Partial<ChecklistHint>
    return { itemId: parsed.itemId ?? null, reviewed: parsed.reviewed ?? false }
  } catch {
    return emptyHint()
  }
}

export function writeChecklistHint(
  userId: string,
  slug: string,
  projectKey: string,
  hint: ChecklistHint,
): void {
  try {
    localStorage.setItem(checklistKey(userId, slug, projectKey), JSON.stringify(hint))
  } catch {
    // A private window loses the memory, not the work.
  }
}
