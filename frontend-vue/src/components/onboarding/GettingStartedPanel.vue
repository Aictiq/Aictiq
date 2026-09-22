<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import { listAgents } from '@/api/agents'
import { getItem } from '@/api/items'
import { getOrganization, type Organization } from '@/api/organizations'
import { getFactorySettings, listPlaybooks } from '@/api/playbooks'
import { canCreateProjects, listProjectMembers } from '@/api/projects'
import { listRunners } from '@/api/runners'
import { listItemRuns } from '@/api/runs'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import {
  readChecklistHint,
  writeChecklistHint,
  type ChecklistHint,
  type ChecklistTaskStatus,
} from '@/lib/onboarding'
import { runnerStatus, runnerStatusLabel } from '@/lib/runners'
import { factoryPath, orgSettingsPath, projectItemsPath, projectSettingsPath } from '@/router/paths'
import { useOnboardingStore } from '@/stores/onboarding'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'
import { useTeamsStore } from '@/stores/teams'
import { ApiError } from '@/utils/api'

/**
 * The resumable "Get started" checklist, a sheet opened from the account menu,
 * the command palette and the tour's calls to action.
 *
 * Readiness is computed when the sheet opens and when the organization, project or team
 * selection changes - never polled. Every check fails alone: one broken request marks its
 * task "could not check" with a Retry, and leaves the rest of the panel working. The AI
 * tasks answer to the same permission rules the API does, so a stakeholder sees "needs an
 * admin" rather than a button that would 403.
 */
const onboarding = useOnboardingStore()
const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const teams = useTeamsStore()
const session = useSessionStore()
const router = useRouter()

const slug = computed(() => organizations.currentSlug)
const projectKey = computed(() => projects.currentKey)
const userId = computed(() => session.user?.id ?? null)
const canOperate = computed(() => organizations.current?.canOperateFactory ?? false)

type TaskId =
  | 'choose-workspace'
  | 'prepare-item'
  | 'prepare-agent'
  | 'configure-repo'
  | 'runner-online'
  | 'prepare-playbook'
  | 'handoff'
  | 'review-result'

interface TaskLink {
  to: string
  label: string
}

interface TaskView {
  status: 'loading' | ChecklistTaskStatus
  detail: string | null
  link: TaskLink | null
  action: 'review' | null
  /** Show the "use this item" picker under this task. */
  pick: boolean
}

/** A check reports through this; a late answer for a stale attempt is dropped. */
type Write = (status: TaskView['status'], extra?: Partial<TaskView>) => void

const emptyTask = (): TaskView => ({ status: 'loading', detail: null, link: null, action: null, pick: false })

const tasks = ref<Record<TaskId, TaskView>>({
  'choose-workspace': emptyTask(),
  'prepare-item': emptyTask(),
  'prepare-agent': emptyTask(),
  'configure-repo': emptyTask(),
  'runner-online': emptyTask(),
  'prepare-playbook': emptyTask(),
  handoff: emptyTask(),
  'review-result': emptyTask(),
})

const taskMeta: Record<TaskId, { title: string; note: string }> = {
  'choose-workspace': {
    title: 'Choose workspace',
    note: 'Pick the organization, project and team the work belongs to - everything else follows that selection.',
  },
  'prepare-item': {
    title: 'Prepare a work item',
    note: 'A good ticket states the outcome, the context and steps, and how to tell it is done. Attach files that help.',
  },
  'prepare-agent': {
    title: 'Prepare an agent',
    note: 'An agent is a bot member of the organization. It also has to be on this project\u2019s roster before it can take an item.',
  },
  'configure-repo': {
    title: 'Configure the repository',
    note: 'Point the project at the repository to change, or at a checkout path on the runner.',
  },
  'runner-online': {
    title: 'Bring a runner online',
    note: 'A runner is a machine that executes runs for your agents.',
  },
  'prepare-playbook': {
    title: 'Prepare a playbook',
    note: 'A playbook points at a wiki page and says which harness the agent uses and when it has succeeded.',
  },
  handoff: {
    title: 'Hand off the selected item',
    note: 'Open the item and use Hand to agent. Only that explicit start creates a run.',
  },
  'review-result': {
    title: 'Review the result',
    note: 'A finished run still needs a human review of the diff, CI and pull request.',
  },
}

const groups: { label: string; tasks: TaskId[] }[] = [
  { label: 'Find your way', tasks: ['choose-workspace', 'prepare-item'] },
  {
    label: 'Prepare AI work',
    tasks: ['prepare-agent', 'configure-repo', 'runner-online', 'prepare-playbook'],
  },
  { label: 'Hand off and review', tasks: ['handoff', 'review-result'] },
]

const chip: Record<TaskView['status'], { label: string; class: string }> = {
  loading: { label: 'Checking…', class: 'text-muted-foreground' },
  ready: { label: 'Ready', class: 'text-success' },
  'needs-setup': { label: 'Needs setup', class: 'text-warning' },
  'needs-admin': { label: 'Needs an admin', class: 'text-muted-foreground' },
  unavailable: { label: 'Could not check', class: 'text-muted-foreground' },
}

const contextLine = computed(() =>
  [organizations.current?.name, projects.current?.name, teams.current?.name]
    .filter(Boolean)
    .join(' · '),
)

const hint = ref<ChecklistHint>({ itemId: null, reviewed: false })

/** One counter per task, so a Retry invalidates only its own task's earlier attempt. */
const attempts: Record<TaskId, number> = {
  'choose-workspace': 0,
  'prepare-item': 0,
  'prepare-agent': 0,
  'configure-repo': 0,
  'runner-online': 0,
  'prepare-playbook': 0,
  handoff: 0,
  'review-result': 0,
}

function writer(id: TaskId): Write {
  const mine = ++attempts[id]
  return (status, extra = {}) => {
    if (mine !== attempts[id]) return
    tasks.value = { ...tasks.value, [id]: { ...emptyTask(), status, ...extra } }
  }
}

/** 403 is "you can see it but may not act", which is an admin's to fix; the rest is ours. */
function failure(error: unknown): ChecklistTaskStatus {
  return error instanceof ApiError && error.status === 403 ? 'needs-admin' : 'unavailable'
}

const itemsLink = computed(() =>
  slug.value && projectKey.value ? projectItemsPath(slug.value, projectKey.value) : '/projects',
)

const itemLink = (itemKey: string) =>
  slug.value && projectKey.value
    ? `/o/${slug.value}/p/${projectKey.value}/items/${itemKey}`
    : itemsLink.value

function checkWorkspace(set: Write) {
  if (organizations.current && projects.current && teams.current) {
    set('ready')
    return
  }

  if (organizations.current && projects.current && !teams.current && slug.value && projectKey.value) {
    set('needs-setup', {
      link: {
        to: projectSettingsPath(slug.value, projectKey.value, 'teams'),
        label: 'Set up a team',
      },
    })
    return
  }

  // Whether the person may create the missing project is the organization's own rule.
  const decide = (org: Organization | null) => {
    const mayCreate = canCreateProjects(
      organizations.current?.role,
      org?.membersCanCreateProjects ?? false,
    )
    if (!projects.current && organizations.current && !mayCreate) {
      set('needs-admin', {
        link: slug.value ? { to: orgSettingsPath(slug.value, 'members'), label: 'Ask an admin' } : null,
      })
    } else {
      set('needs-setup', { link: { to: '/projects', label: 'Choose a project' } })
    }
  }

  if (!organizations.current || !slug.value) {
    decide(null)
    return
  }
  void getOrganization(slug.value).then(decide, () => decide(null))
}

function checkPreparedItem(set: Write) {
  if (!slug.value || !projectKey.value || !userId.value) {
    set('needs-setup', { link: { to: itemsLink.value, label: 'Open items' } })
    return
  }
  const key = hint.value.itemId ?? null
  if (!key) {
    set('needs-setup', { pick: true, link: { to: itemsLink.value, label: 'Browse items' } })
    return
  }
  void getItem(slug.value, key).then(
    (item) => set('ready', { detail: `${item.key} - ${item.title}`, pick: true }),
    () => {
      // The remembered item is gone or no longer visible - it has to be chosen again.
      forgetItem()
      set('needs-setup', { pick: true, link: { to: itemsLink.value, label: 'Browse items' } })
    },
  )
}

/** The chosen item is a note to self, so it is checked against the API before it counts. */
const picking = ref(false)
const pickKey = ref('')
const pickError = ref<string | null>(null)

function chooseItem() {
  const key = pickKey.value.trim().toUpperCase()
  if (!key || !slug.value || !projectKey.value || !userId.value) return
  picking.value = true
  pickError.value = null
  void getItem(slug.value, key).then(
    (item) => {
      picking.value = false
      pickKey.value = ''
      rememberItem(item.key)
    },
    (error) => {
      picking.value = false
      pickError.value =
        error instanceof ApiError && error.status === 404
          ? `No item ${key} you can open.`
          : 'Could not check that item.'
    },
  )
}

/**
 * Choosing (or losing) the item resets the two tasks that hang off it, because a
 * different item has a different handoff and a different result to review.
 */
function rememberItem(key: string | null) {
  if (!slug.value || !projectKey.value || !userId.value) return
  writeChecklistHint(userId.value, slug.value, projectKey.value, { itemId: key, reviewed: false })
  hint.value = { itemId: key, reviewed: false }
  checkPreparedItem(writer('prepare-item'))
  checkHandoff(writer('handoff'))
  checkReview(writer('review-result'))
}

function forgetItem() {
  if (!slug.value || !projectKey.value || !userId.value) return
  writeChecklistHint(userId.value, slug.value, projectKey.value, { itemId: null, reviewed: false })
  hint.value = { itemId: null, reviewed: false }
}

function checkAgent(set: Write) {
  if (!slug.value || !projectKey.value) {
    set('needs-setup')
    return
  }
  const agentsLink = { to: orgSettingsPath(slug.value, 'agents'), label: 'Open agents' }
  void Promise.all([listAgents(slug.value), listProjectMembers(slug.value, projectKey.value)]).then(
    ([agents, members]) => {
      const memberIds = new Set(members.map((member) => member.userId))
      const readyAgent = agents.find((agent) => agent.isActive && memberIds.has(agent.userId))
      if (readyAgent) set('ready', { detail: readyAgent.displayName })
      else set('needs-setup', { link: agentsLink })
    },
    (error) => {
      const status = failure(error)
      set(status, { link: status === 'unavailable' ? null : agentsLink })
    },
  )
}

function checkRepository(set: Write) {
  if (!slug.value || !projectKey.value) {
    set('needs-setup')
    return
  }
  const settingsLink = {
    to: projectSettingsPath(slug.value, projectKey.value, 'factory'),
    label: 'Open factory settings',
  }
  void getFactorySettings(slug.value, projectKey.value).then(
    (settings) => {
      // A project that was never configured answers with a synthesized runner-local
      // default (no row, so no `updatedAt`). Treating that as ready would report a
      // configuration nobody made; a GitHub binding needs the repository named, and a
      // runner-local choice needs to have actually been saved.
      const configured =
        settings.repoSource === 1
          ? settings.updatedAt !== null
          : (settings.repoFullName ?? '').trim().length > 0
      set(configured ? 'ready' : 'needs-setup', {
        detail: !configured
          ? null
          : settings.repoSource === 1
            ? `Runner-local checkout${settings.localPathHint ? ` (${settings.localPathHint})` : ''} - the runner needs a matching path on its own machine.`
            : settings.repoFullName,
        link: configured ? null : settingsLink,
      })
    },
    (error) => {
      const status = failure(error)
      set(status, { link: status === 'unavailable' ? null : settingsLink })
    },
  )
}

function checkRunner(set: Write) {
  if (!canOperate.value) {
    set('needs-admin')
    return
  }
  if (!slug.value) {
    set('needs-setup')
    return
  }
  const runnersLink = { to: factoryPath(slug.value, 'runners'), label: 'Open runners' }
  void listRunners(slug.value).then(
    (runners) => {
      if (runners.some((runner) => runnerStatus(runner) === 'online')) {
        set('ready')
        return
      }
      const waiting = runners.find((runner) => runnerStatus(runner) !== 'disabled')
      set('needs-setup', { detail: waiting ? runnerStatusLabel[runnerStatus(waiting)] : null, link: runnersLink })
    },
    (error) => {
      const status = failure(error)
      set(status, { link: status === 'unavailable' ? null : runnersLink })
    },
  )
}

function checkPlaybook(set: Write) {
  if (!canOperate.value) {
    set('needs-admin')
    return
  }
  if (!slug.value || !projectKey.value) {
    set('needs-setup')
    return
  }
  const playbooksLink = { to: factoryPath(slug.value, 'playbooks'), label: 'Open playbooks' }
  void listPlaybooks(slug.value, projectKey.value).then(
    (playbooks) => {
      const any = playbooks.length > 0
      set(any ? 'ready' : 'needs-setup', { link: any ? null : playbooksLink })
    },
    (error) => {
      const status = failure(error)
      set(status, { link: status === 'unavailable' ? null : playbooksLink })
    },
  )
}

function checkHandoff(set: Write) {
  if (!canOperate.value) {
    set('needs-admin')
    return
  }
  const key = hint.value.itemId ?? null
  if (!key) {
    set('needs-setup', { link: { to: itemsLink.value, label: 'Choose an item' } })
    return
  }
  if (!slug.value) {
    set('needs-setup', { link: { to: itemLink(key), label: `Open ${key}` } })
    return
  }
  void listItemRuns(slug.value, key, 1, 1).then(
    (runs) => {
      const any = runs.totalCount > 0
      set(any ? 'ready' : 'needs-setup', {
        detail: any ? key : null,
        link: any ? null : { to: itemLink(key), label: `Open ${key}` },
      })
    },
    (error) => {
      const status = failure(error)
      set(status === 'unavailable' ? 'needs-setup' : status, {
        link: { to: itemLink(key), label: `Open ${key}` },
      })
    },
  )
}

function checkReview(set: Write) {
  const done = hint.value.reviewed === true
  const key = hint.value.itemId ?? null
  set(done ? 'ready' : 'needs-setup', {
    action: done ? null : 'review',
    detail: done && key ? `Reviewed ${key}.` : null,
    // The run, its outcome comment and any pull request all live on the item.
    link: key ? { to: itemLink(key), label: `Open ${key}` } : null,
  })
}

const checks: Record<TaskId, (set: Write) => void> = {
  'choose-workspace': checkWorkspace,
  'prepare-item': checkPreparedItem,
  'prepare-agent': checkAgent,
  'configure-repo': checkRepository,
  'runner-online': checkRunner,
  'prepare-playbook': checkPlaybook,
  handoff: checkHandoff,
  'review-result': checkReview,
}

/** Re-reads the hint, resets every task, and runs every check. */
function refresh() {
  hint.value =
    slug.value && projectKey.value && userId.value
      ? readChecklistHint(userId.value, slug.value, projectKey.value)
      : { itemId: null, reviewed: false }
  pickKey.value = ''
  pickError.value = null

  for (const id of Object.keys(tasks.value) as TaskId[]) {
    tasks.value[id] = emptyTask()
    checks[id](writer(id))
  }
}

function retry(id: TaskId) {
  tasks.value[id] = emptyTask()
  checks[id](writer(id))
}

function markReviewed() {
  if (!slug.value || !projectKey.value || !userId.value) return
  writeChecklistHint(userId.value, slug.value, projectKey.value, {
    ...readChecklistHint(userId.value, slug.value, projectKey.value),
    reviewed: true,
  })
  hint.value = { ...hint.value, reviewed: true }
  checkReview(writer('review-result'))
}

watch(
  [() => onboarding.checklistOpen, slug, projectKey, () => teams.currentId, userId],
  () => {
    if (onboarding.checklistOpen) refresh()
  },
  { immediate: true },
)
</script>

<template>
  <Sheet
    :open="onboarding.checklistOpen"
    @update:open="(open) => (open ? onboarding.openChecklist() : onboarding.closeChecklist())"
  >
    <SheetContent side="right" class="w-full gap-0 sm:max-w-md">
      <SheetHeader class="gap-1.5 border-b pr-10">
        <SheetTitle>Get started</SheetTitle>
        <SheetDescription>
          A short path from empty project to a reviewed agent run. Leave and come back -
          this picks up where you stopped.
        </SheetDescription>
        <p v-if="contextLine" class="text-muted-foreground text-xs">{{ contextLine }}</p>
      </SheetHeader>

      <div class="flex-1 overflow-y-auto px-4 py-2">
        <section v-for="group in groups" :key="group.label" class="py-2">
          <h3 class="font-label text-muted-foreground pb-1">{{ group.label }}</h3>
          <ul>
            <li
              v-for="id in group.tasks"
              :key="id"
              class="border-border/60 flex flex-col gap-1 border-b py-3 last:border-0"
            >
              <div class="flex items-center justify-between gap-2">
                <span class="text-sm font-medium">{{ taskMeta[id].title }}</span>
                <span class="text-xs" :class="chip[tasks[id].status].class">
                  {{ chip[tasks[id].status].label }}
                </span>
              </div>
              <p class="text-muted-foreground text-xs">{{ taskMeta[id].note }}</p>
              <p v-if="tasks[id].detail" class="truncate text-xs">{{ tasks[id].detail }}</p>
              <div class="mt-0.5 flex items-center gap-2">
                <Button
                  v-if="tasks[id].link"
                  type="button"
                  variant="outline"
                  size="xs"
                  @click="router.push(tasks[id].link!.to)"
                >
                  {{ tasks[id].link!.label }}
                </Button>
                <Button
                  v-if="tasks[id].action === 'review'"
                  type="button"
                  variant="outline"
                  size="xs"
                  @click="markReviewed"
                >
                  I reviewed the result
                </Button>
                <Button
                  v-if="tasks[id].status === 'unavailable'"
                  type="button"
                  variant="ghost"
                  size="xs"
                  @click="retry(id)"
                >
                  Retry
                </Button>
                <Button
                  v-if="tasks[id].pick && hint.itemId"
                  type="button"
                  variant="ghost"
                  size="xs"
                  @click="rememberItem(null)"
                >
                  Choose another
                </Button>
              </div>

              <!-- Naming the item is the person's own choice, and it is checked against
                   the API before it counts: a key they cannot open is not a selection. -->
              <form
                v-if="tasks[id].pick && !hint.itemId"
                class="mt-1 flex items-start gap-2"
                @submit.prevent="chooseItem"
              >
                <div class="flex-1">
                  <Input
                    v-model="pickKey"
                    :aria-label="`Item key to prepare in ${projects.current?.name ?? 'this project'}`"
                    :placeholder="projectKey ? `${projectKey}-1` : 'Item key'"
                    class="h-7 text-xs"
                  />
                  <p v-if="pickError" class="text-destructive mt-1 text-xs">{{ pickError }}</p>
                </div>
                <Button type="submit" variant="outline" size="xs" :disabled="picking || !pickKey.trim()">
                  Use this item
                </Button>
              </form>
            </li>
          </ul>
        </section>
      </div>

      <div class="border-border flex items-center justify-between gap-2 border-t px-4 py-3">
        <Button
          v-if="onboarding.loadFailed"
          type="button"
          variant="secondary"
          size="sm"
          @click="onboarding.openChecklist()"
        >
          Retry
        </Button>
        <span v-else />
        <Button type="button" variant="ghost" size="sm" @click="onboarding.startTour()">
          Replay product tour
        </Button>
      </div>
    </SheetContent>
  </Sheet>
</template>
