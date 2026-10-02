<script setup lang="ts">
import { ChevronLeft, ChevronRight, ExternalLink, X } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'

import type { ListRunsOptions, RunKind, RunStatsGrouping, RunStatus } from '@/api/runs'
import { getRunStats, listRuns } from '@/api/runs'
import { listAgents } from '@/api/agents'
import { listProjects } from '@/api/projects'
import EmptyState from '@/components/common/EmptyState.vue'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import RunStats, { type RunStatsDrill } from '@/components/factory/RunStats.vue'
import RunStatusBadge from '@/components/factory/RunStatusBadge.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useOrgScope } from '@/composables/useSettingsScope'
import {
  isLiveRun,
  projectKeyOf,
  runDuration,
  runRequesterLabel,
  runScheduledLabel,
  runStatuses,
} from '@/lib/runs'
import {
  defaultRunRange,
  runRangeLabel,
  runRanges,
  runWindow,
  startOfLocalDay,
  type RunRange,
} from '@/lib/runStats'
import { factoryPath } from '@/router/paths'

/**
 * The Factory's Runs tab: how the factory is doing, then every run in the organization,
 * newest first, filtered down to the one someone is looking for. The filters live in the
 * URL - "the failed runs on PROJ this week" is a link someone pastes, not a state to
 * recreate by hand - and the statistics and the list read the same ones, so a figure
 * always counts the runs listed under it.
 *
 * Live status is a quiet refetch, not a push: this page is not inside any one project's
 * hub group, and a run that flips from queued to running a few seconds late is fine.
 */
const org = useOrgScope()
const route = useRoute()
const router = useRouter()

const slug = computed(() => org.slug.value)

const runKinds: { value: RunKind; label: string }[] = [
  { value: 'implement', label: 'Implement' },
  { value: 'refine', label: 'Refine' },
]

interface Filters {
  project: string
  agent: string
  status: string
  item: string
  kind: string
  playbook: string
  runner: string
  range: RunRange
  /** `yyyy-mm-dd` - a day range picked from a chart, which takes over from the preset. */
  fromDay: string
  toDay: string
  failure: string
  page: number
}

const text = (value: unknown) => (typeof value === 'string' ? value : '')
const day = (value: unknown) => (typeof value === 'string' && startOfLocalDay(value) ? value : '')

function filtersFromRoute(): Filters {
  const query = route.query
  const status =
    typeof query.status === 'string' && (runStatuses as string[]).includes(query.status)
      ? query.status
      : ''
  const kind =
    typeof query.kind === 'string' && runKinds.some((option) => option.value === query.kind)
      ? query.kind
      : ''
  const range =
    typeof query.range === 'string' && (runRanges as readonly string[]).includes(query.range)
      ? (query.range as RunRange)
      : defaultRunRange
  const page = Number.parseInt(String(query.page ?? ''), 10)
  return {
    project: text(query.project),
    agent: text(query.agent),
    status,
    item: text(query.itemKey),
    kind,
    playbook: text(query.playbook),
    runner: text(query.runner),
    range,
    fromDay: day(query.from),
    toDay: day(query.to),
    failure: text(query.failure),
    page: Number.isFinite(page) && page > 0 ? page : 1,
  }
}

const filters = ref<Filters>(filtersFromRoute())

watch(
  () => route.query,
  () => {
    filters.value = filtersFromRoute()
  },
)

function applyFilters(next: Partial<Filters>) {
  const merged = { ...filters.value, ...next }
  const query: Record<string, string> = {}
  if (merged.project) query.project = merged.project
  if (merged.agent) query.agent = merged.agent
  if (merged.status) query.status = merged.status
  // `itemKey`, not `item`: `?item=` is the app-wide item peek, and would open it over the list.
  if (merged.item) query.itemKey = merged.item
  if (merged.kind) query.kind = merged.kind
  if (merged.playbook) query.playbook = merged.playbook
  if (merged.runner) query.runner = merged.runner
  if (merged.range !== defaultRunRange) query.range = merged.range
  if (merged.fromDay) query.from = merged.fromDay
  if (merged.toDay) query.to = merged.toDay
  if (merged.failure) query.failure = merged.failure
  if (merged.page > 1) query.page = String(merged.page)
  // A filter change restarts the list; the route is the one place the state lives.
  void router.replace({ query })
}

const noFilters = {
  project: '',
  agent: '',
  status: '',
  item: '',
  kind: '',
  playbook: '',
  runner: '',
  range: defaultRunRange,
  fromDay: '',
  toDay: '',
  failure: '',
  page: 1,
} satisfies Filters

/** Narrowing beyond the default view. The default 30 days is not a filter someone set. */
const hasFilters = computed(() => {
  const current = filters.value
  return (Object.keys(noFilters) as (keyof Filters)[]).some(
    (key) => key !== 'page' && current[key] !== noFilters[key],
  )
})

const hasDays = computed(() => filters.value.fromDay !== '' || filters.value.toDay !== '')

const dayFormat = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' })
const daysLabel = computed(() => {
  const from = startOfLocalDay(filters.value.fromDay)
  const to = startOfLocalDay(filters.value.toDay)
  if (from && to && filters.value.fromDay === filters.value.toDay) return dayFormat.format(from)
  return `${from ? dayFormat.format(from) : '…'} – ${to ? dayFormat.format(to) : 'now'}`
})

function onRangeChange(event: Event) {
  const value = (event.target as HTMLSelectElement).value
  if (value === 'custom') return
  applyFilters({ range: value as RunRange, fromDay: '', toDay: '', page: 1 })
}

function onDrill(drill: RunStatsDrill) {
  const next: Partial<Filters> = { page: 1 }
  if (drill.status) next.status = drill.status
  if (drill.fromDay) next.fromDay = drill.fromDay
  if (drill.toDay) next.toDay = drill.toDay
  if (drill.agent) next.agent = drill.agent
  if (drill.project) next.project = drill.project
  if (drill.playbook) next.playbook = drill.playbook
  if (drill.runner) next.runner = drill.runner
  if (drill.failure) next.failure = drill.failure
  applyFilters(next)
}

// The window is worked out once per filter change, not on every render: a rolling "now"
// in the query key would refetch for ever.
const timeWindow = computed(() =>
  runWindow(filters.value.range, { from: filters.value.fromDay, to: filters.value.toDay }),
)

const filterOptions = computed<ListRunsOptions>(() => ({
  project: filters.value.project || undefined,
  agent: filters.value.agent || undefined,
  status: (filters.value.status || undefined) as RunStatus | undefined,
  item: filters.value.item || undefined,
  kind: (filters.value.kind || undefined) as RunKind | undefined,
  playbook: filters.value.playbook || undefined,
  runner: filters.value.runner || undefined,
  from: timeWindow.value.from,
  to: timeWindow.value.to,
  failure: filters.value.failure || undefined,
}))

const projects = useQuery({
  queryKey: computed(() => ['projects', slug.value, 'factory-filter']),
  queryFn: () => listProjects(slug.value, true),
})
const agents = useQuery({
  queryKey: computed(() => ['agents', slug.value, 'factory-filter']),
  queryFn: () => listAgents(slug.value),
})

const groupBy = ref<RunStatsGrouping>('agent')
const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone

const stats = useQuery({
  queryKey: computed(() => ['runs', slug.value, 'stats', filterOptions.value, groupBy.value]),
  queryFn: () =>
    getRunStats(slug.value, { ...filterOptions.value, groupBy: groupBy.value, tz: timeZone }),
  placeholderData: keepPreviousData,
  refetchInterval: (query) => ((query.state.data?.active ?? 0) > 0 ? 30_000 : false),
})

const runs = useQuery({
  queryKey: computed(() => ['runs', slug.value, filterOptions.value, filters.value.page]),
  queryFn: () =>
    listRuns(slug.value, { ...filterOptions.value, page: filters.value.page, pageSize: 25 }),
  placeholderData: keepPreviousData,
  // A page with live work on it keeps itself fresh; a quiet page does not need to.
  refetchInterval: (query) => {
    const data = query.state.data
    return data && data.items.some((run) => isLiveRun(run.status)) ? 15_000 : false
  },
})

const runsPage = computed(() => runs.data.value)
const runRows = computed(() => runsPage.value?.items ?? [])
const totalPages = computed(() => Math.max(1, Math.ceil((runsPage.value?.totalCount ?? 0) / 25)))

// The default 30 days can be empty in an organization that ran agents before then; only an
// organization that never ran anything gets the first-run steps.
const everRan = useQuery({
  queryKey: computed(() => ['runs', slug.value, 'ever']),
  queryFn: () => listRuns(slug.value, { pageSize: 1 }),
  enabled: computed(() => !hasFilters.value && runs.isSuccess.value && runRows.value.length === 0),
})
const neverRan = computed(() => !hasFilters.value && everRan.data.value?.totalCount === 0)

const playbookOptions = computed(() => stats.data.value?.playbooks ?? [])
const runnerOptions = computed(() => stats.data.value?.runners ?? [])

const itemPath = (run: { itemKey: string }) =>
  `/o/${slug.value}/p/${projectKeyOf(run.itemKey)}/items/${run.itemKey}`

/** "Rule: Start implementation" when a rule dispatched the run rather than a person. */
const requesterLabel = runRequesterLabel

const itemInput = ref(filters.value.item)
watch(
  () => filters.value.item,
  (value) => {
    itemInput.value = value
  },
)
</script>

<template>
  <div class="space-y-4">
    <header class="pb-1">
      <h2 class="text-sm font-medium">Runs</h2>
      <p class="text-muted-foreground mt-0.5 text-xs">
        Every run in this organization, newest first. A run starts when someone hands an item to an
        agent from the item's page.
      </p>
    </header>

    <div class="flex flex-wrap items-center gap-2" data-testid="runs-filters">
      <select
        v-model="filters.project"
        aria-label="Filter by project"
        class="border-input bg-background w-44 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ project: filters.project, page: 1 })"
      >
        <option value="">All projects</option>
        <option v-for="project in projects.data.value ?? []" :key="project.id" :value="project.key">
          {{ project.name }}
        </option>
      </select>
      <select
        v-model="filters.agent"
        aria-label="Filter by agent"
        class="border-input bg-background w-44 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ agent: filters.agent, page: 1 })"
      >
        <option value="">All agents</option>
        <option v-for="agent in agents.data.value ?? []" :key="agent.userId" :value="agent.userId">
          {{ agent.displayName }}
        </option>
      </select>
      <select
        v-model="filters.status"
        aria-label="Filter by status"
        class="border-input bg-background w-36 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ status: filters.status, page: 1 })"
      >
        <option value="">Any status</option>
        <option v-for="status in runStatuses" :key="status" :value="status">
          {{ status === 'timedOut' ? 'Timed out' : status[0]!.toUpperCase() + status.slice(1) }}
        </option>
      </select>
      <input
        v-model="itemInput"
        placeholder="Item key - PROJ-12"
        aria-label="Filter by item key"
        class="border-input bg-background w-44 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ item: itemInput.trim(), page: 1 })"
      />
      <select
        v-model="filters.kind"
        aria-label="Filter by kind"
        class="border-input bg-background w-36 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ kind: filters.kind, page: 1 })"
      >
        <option value="">Any kind</option>
        <option v-for="kind in runKinds" :key="kind.value" :value="kind.value">
          {{ kind.label }}
        </option>
      </select>
      <select
        v-model="filters.playbook"
        aria-label="Filter by playbook"
        class="border-input bg-background w-44 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ playbook: filters.playbook, page: 1 })"
      >
        <option value="">All playbooks</option>
        <option v-for="playbook in playbookOptions" :key="playbook.id" :value="playbook.id">
          {{ playbook.name }}
        </option>
        <option
          v-if="
            filters.playbook &&
            !playbookOptions.some((playbook) => playbook.id === filters.playbook)
          "
          :value="filters.playbook"
        >
          Other playbook
        </option>
      </select>
      <select
        v-model="filters.runner"
        aria-label="Filter by runner"
        class="border-input bg-background w-40 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ runner: filters.runner, page: 1 })"
      >
        <option value="">All runners</option>
        <option v-for="runner in runnerOptions" :key="runner.id" :value="runner.id">
          {{ runner.name }}
        </option>
        <option
          v-if="filters.runner && !runnerOptions.some((runner) => runner.id === filters.runner)"
          :value="filters.runner"
        >
          Other runner
        </option>
      </select>
      <select
        :value="hasDays ? 'custom' : filters.range"
        aria-label="Filter by date range"
        class="border-input bg-background w-40 rounded border px-2 py-1.5 text-sm"
        @change="onRangeChange"
      >
        <option v-for="range in runRanges" :key="range" :value="range">
          {{ runRangeLabel[range] }}
        </option>
        <option v-if="hasDays" value="custom">{{ daysLabel }}</option>
      </select>
      <span
        v-if="filters.failure"
        class="border-border bg-muted inline-flex max-w-72 items-center gap-1 rounded border py-1 pr-1 pl-2 text-xs"
        data-testid="runs-filter-failure"
      >
        <span class="truncate"
          >Failure: <span class="font-mono">{{ filters.failure }}</span></span
        >
        <button
          type="button"
          class="hover:bg-background rounded p-0.5"
          aria-label="Remove the failure reason filter"
          @click="applyFilters({ failure: '', page: 1 })"
        >
          <X class="size-3" aria-hidden="true" />
        </button>
      </span>
      <Button
        v-if="hasFilters"
        variant="ghost"
        size="sm"
        data-testid="runs-filters-clear"
        @click="applyFilters(noFilters)"
      >
        Clear
      </Button>
    </div>

    <RunStats
      v-model:group-by="groupBy"
      :stats="stats.data.value"
      :loading="stats.isPending.value"
      :error="stats.isError.value"
      :window="timeWindow"
      @drill="onDrill"
    />

    <UiPageState v-if="runs.isPending.value" state="loading" />
    <p v-else-if="runs.isError.value" class="text-destructive text-sm">Runs could not be loaded.</p>

    <UiPageState
      v-else-if="runRows.length === 0 && !hasFilters && everRan.isPending.value"
      state="loading"
    />

    <EmptyState
      v-else-if="runRows.length === 0 && neverRan"
      title="No runs yet"
      icon="◇"
      description="Three steps and the factory is working: register a runner (a machine with the harness signed in), write a playbook (the wiki page an agent follows), then hand any item to an agent from its page."
    >
      <div class="flex flex-wrap justify-center gap-2">
        <Button variant="secondary" @click="$router.push(factoryPath(slug, 'runners'))">
          Register a runner
        </Button>
        <Button variant="secondary" @click="$router.push(factoryPath(slug, 'playbooks'))">
          Create a playbook
        </Button>
        <FactoryDocsLink />
      </div>
    </EmptyState>

    <EmptyState
      v-else-if="runRows.length === 0"
      :title="hasFilters ? 'No runs match these filters' : 'No runs in the last 30 days'"
      :description="
        hasFilters
          ? 'Nothing has run with these filters together.'
          : 'Pick a longer date range to see older runs.'
      "
      icon="◇"
    />

    <template v-else>
      <ul class="border-border divide-border divide-y rounded-lg border" data-testid="runs-list">
        <li v-for="run in runRows" :key="run.id">
          <button
            type="button"
            class="hover:bg-muted flex w-full flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2.5 text-left"
            @click="$router.push(`/o/${slug}/factory/runs/${run.id}`)"
          >
            <RunStatusBadge :status="run.status" />
            <RouterLink
              :to="itemPath(run)"
              class="text-muted-foreground font-mono text-xs underline-offset-2 hover:underline"
              @click.stop
            >
              {{ run.itemKey }}
            </RouterLink>
            <span class="min-w-0 truncate text-sm font-medium">{{ run.agentName ?? 'Agent' }}</span>
            <span class="text-muted-foreground min-w-0 truncate text-xs">
              {{ run.playbookName ?? '-' }}
            </span>
            <span v-if="requesterLabel(run)" class="text-muted-foreground shrink-0 text-xs italic">
              {{ requesterLabel(run) }}
            </span>
            <span
              v-if="runScheduledLabel(run)"
              class="text-muted-foreground shrink-0 text-xs"
              data-testid="run-scheduled"
              >{{ runScheduledLabel(run) }}</span
            >
            <span
              v-if="run.continuesRunId"
              class="text-muted-foreground shrink-0 text-xs"
              data-testid="run-row-continues"
            >
              {{ run.autoContinued ? 'auto-continue' : 'continue' }}
            </span>
            <span
              v-if="run.continuedByRunId"
              class="text-muted-foreground shrink-0 text-xs"
              data-testid="run-row-continued"
            >
              → continued
            </span>
            <span class="ml-auto flex items-center gap-3">
              <a
                v-if="run.pullRequestUrl"
                :href="run.pullRequestUrl"
                target="_blank"
                rel="noreferrer"
                class="text-muted-foreground hover:text-foreground"
                :title="'Pull request'"
                @click.stop
              >
                <ExternalLink class="size-3.5" aria-hidden="true" />
              </a>
              <span class="text-muted-foreground text-xs">{{
                runDuration(run) ?? `queued ${new Date(run.queuedAt).toLocaleDateString()}`
              }}</span>
            </span>
          </button>
        </li>
      </ul>

      <div class="text-muted-foreground flex items-center justify-between text-xs">
        <span>
          {{ runsPage?.totalCount ?? 0 }} run{{ (runsPage?.totalCount ?? 0) === 1 ? '' : 's' }} ·
          page {{ filters.page }} of {{ totalPages }}
        </span>
        <div class="flex gap-2">
          <Button
            variant="outline"
            size="sm"
            :disabled="filters.page <= 1"
            data-testid="runs-prev"
            @click="applyFilters({ page: filters.page - 1 })"
          >
            <ChevronLeft class="size-4" aria-hidden="true" /> Previous
          </Button>
          <Button
            variant="outline"
            size="sm"
            :disabled="filters.page >= totalPages"
            data-testid="runs-next"
            @click="applyFilters({ page: filters.page + 1 })"
          >
            Next <ChevronRight class="size-4" aria-hidden="true" />
          </Button>
        </div>
      </div>
    </template>
  </div>
</template>
