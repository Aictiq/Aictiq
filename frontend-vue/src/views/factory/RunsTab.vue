<script setup lang="ts">
import { ChevronLeft, ChevronRight, ExternalLink } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'

import type { RunStatus } from '@/api/runs'
import { listRuns } from '@/api/runs'
import { listAgents } from '@/api/agents'
import { listProjects } from '@/api/projects'
import EmptyState from '@/components/common/EmptyState.vue'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import RunStatusBadge from '@/components/factory/RunStatusBadge.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useOrgScope } from '@/composables/useSettingsScope'
import { isLiveRun, projectKeyOf, runDuration, runRequesterLabel, runStatuses } from '@/lib/runs'
import { factoryPath } from '@/router/paths'

/**
 * The Factory's Runs tab: every run in the organization, newest first, filtered down to
 * the one someone is looking for. The filters live in the URL — "the failed runs on
 * PROJ this week" is a link someone pastes, not a state to recreate by hand.
 *
 * Live status is a quiet refetch, not a push: this page is not inside any one project's
 * hub group, and a run that flips from queued to running a few seconds late is fine.
 */
const org = useOrgScope()
const route = useRoute()
const router = useRouter()

const slug = computed(() => org.slug.value)

interface Filters {
  project: string
  agent: string
  status: string
  item: string
  page: number
}

function filtersFromRoute(): Filters {
  const query = route.query
  const status =
    typeof query.status === 'string' && (runStatuses as string[]).includes(query.status)
      ? query.status
      : ''
  const page = Number.parseInt(String(query.page ?? ''), 10)
  return {
    project: typeof query.project === 'string' ? query.project : '',
    agent: typeof query.agent === 'string' ? query.agent : '',
    status,
    item: typeof query.itemKey === 'string' ? query.itemKey : '',
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
  if (merged.page > 1) query.page = String(merged.page)
  // A filter change restarts the list; the route is the one place the state lives.
  void router.replace({ query })
}

const hasFilters = computed(
  () =>
    filters.value.project !== '' ||
    filters.value.agent !== '' ||
    filters.value.status !== '' ||
    filters.value.item !== '',
)

const projects = useQuery({
  queryKey: computed(() => ['projects', slug.value, 'factory-filter']),
  queryFn: () => listProjects(slug.value, true),
})
const agents = useQuery({
  queryKey: computed(() => ['agents', slug.value, 'factory-filter']),
  queryFn: () => listAgents(slug.value),
})

const runs = useQuery({
  queryKey: computed(() => [
    'runs',
    slug.value,
    filters.value.project,
    filters.value.agent,
    filters.value.status,
    filters.value.item,
    filters.value.page,
  ]),
  queryFn: () =>
    listRuns(slug.value, {
      project: filters.value.project || undefined,
      agent: filters.value.agent || undefined,
      status: (filters.value.status || undefined) as RunStatus | undefined,
      item: filters.value.item || undefined,
      page: filters.value.page,
      pageSize: 25,
    }),
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
        placeholder="Item key — PROJ-12"
        aria-label="Filter by item key"
        class="border-input bg-background w-44 rounded border px-2 py-1.5 text-sm"
        @change="applyFilters({ item: itemInput.trim(), page: 1 })"
      />
      <Button
        v-if="hasFilters"
        variant="ghost"
        size="sm"
        data-testid="runs-filters-clear"
        @click="applyFilters({ project: '', agent: '', status: '', item: '', page: 1 })"
      >
        Clear
      </Button>
    </div>

    <UiPageState v-if="runs.isPending.value" state="loading" />
    <p v-else-if="runs.isError.value" class="text-destructive text-sm">Runs could not be loaded.</p>

    <EmptyState
      v-else-if="runRows.length === 0 && !hasFilters"
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
      title="No runs match these filters"
      description="Nothing has run with this project, agent, status and item together."
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
              {{ run.playbookName ?? '—' }}
            </span>
            <span v-if="requesterLabel(run)" class="text-muted-foreground shrink-0 text-xs italic">
              {{ requesterLabel(run) }}
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
