<script setup lang="ts">
import { ArrowDown, ArrowUp, ChevronDown, ChevronRight } from '@lucide/vue'
import type { ECElementEvent } from 'echarts'
import { computed, ref, watch } from 'vue'
import VChart from 'vue-echarts'

import type { RunStats, RunStatsGroup, RunStatsGrouping, RunStatus } from '@/api/runs'
import { runStatuses } from '@/lib/runs'
import {
  costPerDayOption,
  formatPercent,
  formatSeconds,
  formatTokens,
  formatUsd,
  runsPerDayOption,
  windowDays,
  type RunWindow,
} from '@/lib/runStats'
import { useSessionStore } from '@/stores/session'

/**
 * The figures above Factory → Runs: a KPI row and, folded away on request, the charts.
 * Everything here counts the runs the list's filters pick out; a click on a chart segment,
 * a breakdown row or a failure reason asks the page to narrow the filters to those runs.
 */
export interface RunStatsDrill {
  status?: RunStatus
  /** `yyyy-mm-dd`, local. */
  fromDay?: string
  toDay?: string
  agent?: string
  project?: string
  playbook?: string
  runner?: string
  failure?: string
}

const props = defineProps<{
  stats: RunStats | undefined
  loading: boolean
  error: boolean
  window: RunWindow
}>()
const groupBy = defineModel<RunStatsGrouping>('groupBy', { required: true })
const emit = defineEmits<{ drill: [RunStatsDrill] }>()

const session = useSessionStore()

// Per person, per browser: someone who folds the charts away wants them folded next time.
const storageKey = computed(() => `aictiq.runStats.chartsOpen.${session.user?.id ?? 'anonymous'}`)
function readOpen(): boolean {
  try {
    return localStorage.getItem(storageKey.value) !== 'false'
  } catch {
    return true
  }
}
const chartsOpen = ref(readOpen())
watch(storageKey, () => {
  chartsOpen.value = readOpen()
})
function toggleCharts() {
  chartsOpen.value = !chartsOpen.value
  try {
    localStorage.setItem(storageKey.value, String(chartsOpen.value))
  } catch {
    // Blocked storage: the choice still holds for this visit.
  }
}

const days = computed(() => (props.stats ? windowDays(props.window, props.stats.days) : []))
const runsOption = computed(() => (props.stats ? runsPerDayOption(props.stats, days.value) : {}))
const costOption = computed(() => (props.stats ? costPerDayOption(props.stats, days.value) : {}))
const hasDays = computed(() => (props.stats?.days.length ?? 0) > 0)

function onRunsClick(params: ECElementEvent) {
  const day = days.value[params.dataIndex]
  const status = runStatuses.find((candidate) => candidate === params.seriesId)
  if (day && status) emit('drill', { status, fromDay: day, toDay: day })
}

function onCostClick(params: ECElementEvent) {
  const day = days.value[params.dataIndex]
  if (day) emit('drill', { fromDay: day, toDay: day })
}

const groupings: { value: RunStatsGrouping; label: string }[] = [
  { value: 'agent', label: 'Agent' },
  { value: 'project', label: 'Project' },
  { value: 'playbook', label: 'Playbook' },
  { value: 'runner', label: 'Runner' },
]

type SortColumn = 'runs' | 'successRate' | 'costUsd' | 'averageCostUsd' | 'medianDurationSeconds'
const sortColumn = ref<SortColumn>('runs')
const sortDescending = ref(true)

const columns: { key: SortColumn; label: string }[] = [
  { key: 'runs', label: 'Runs' },
  { key: 'successRate', label: 'Success' },
  { key: 'costUsd', label: 'Total cost' },
  { key: 'averageCostUsd', label: 'Avg cost' },
  { key: 'medianDurationSeconds', label: 'Median duration' },
]

function sortBy(column: SortColumn) {
  if (sortColumn.value === column) sortDescending.value = !sortDescending.value
  else {
    sortColumn.value = column
    sortDescending.value = true
  }
}

function sortValue(group: RunStatsGroup, column: SortColumn): number | null {
  if (column === 'successRate')
    return group.finished === 0 ? null : group.succeeded / group.finished
  return group[column]
}

const sortedGroups = computed(() => {
  const groups = [...(props.stats?.groups ?? [])]
  const direction = sortDescending.value ? -1 : 1
  // Groups with no figure sort last whichever way the column runs.
  return groups.sort((a, b) => {
    const left = sortValue(a, sortColumn.value)
    const right = sortValue(b, sortColumn.value)
    if (left === null || right === null) return left === right ? 0 : left === null ? 1 : -1
    return (left - right) * direction
  })
})

function groupLabel(group: RunStatsGroup): string {
  if (group.key === null) return groupBy.value === 'runner' ? 'Not taken by a runner' : '–'
  return group.name ?? (groupBy.value === 'project' ? group.key : 'Deleted')
}

function drillGroup(group: RunStatsGroup) {
  if (group.key === null) return
  emit('drill', { [groupBy.value]: group.key })
}

const kpis = computed(() => {
  const stats = props.stats
  if (!stats) return []
  return [
    {
      label: 'Runs',
      value: String(stats.total),
      hint: `${stats.active} active`,
      testid: 'kpi-runs',
    },
    {
      label: 'Success rate',
      value: formatPercent(stats.succeeded, stats.finished),
      hint: `${stats.succeeded} of ${stats.finished} finished`,
      testid: 'kpi-success',
    },
    {
      label: 'Cost',
      value: formatUsd(stats.totalCostUsd),
      hint: `${formatUsd(stats.averageCostUsd)} per finished run`,
      testid: 'kpi-cost',
    },
    {
      label: 'Tokens',
      value: `${formatTokens(stats.inputTokens)} in`,
      hint: `${formatTokens(stats.outputTokens)} out`,
      testid: 'kpi-tokens',
    },
    {
      label: 'Duration',
      value: formatSeconds(stats.medianDurationSeconds),
      hint: `median · p90 ${formatSeconds(stats.p90DurationSeconds)}`,
      testid: 'kpi-duration',
    },
    {
      label: 'Queue wait',
      value: formatSeconds(stats.medianQueueWaitSeconds),
      hint: 'median',
      testid: 'kpi-wait',
    },
    {
      label: 'PRs opened',
      value: String(stats.pullRequests),
      hint: 'runs with a pull request',
      testid: 'kpi-prs',
    },
  ]
})
</script>

<template>
  <section class="space-y-3" data-testid="run-stats">
    <p v-if="error" class="text-destructive text-sm">Run statistics could not be loaded.</p>
    <div
      v-else-if="loading && !stats"
      class="text-muted-foreground border-border rounded-lg border px-3 py-6 text-center text-xs"
    >
      Loading statistics…
    </div>
    <template v-else-if="stats">
      <dl class="grid grid-cols-2 gap-2 sm:grid-cols-4 xl:grid-cols-7" data-testid="run-stats-kpis">
        <div
          v-for="kpi in kpis"
          :key="kpi.label"
          class="border-border bg-card rounded-lg border px-3 py-2"
          :data-testid="kpi.testid"
        >
          <dt class="text-muted-foreground text-xs">{{ kpi.label }}</dt>
          <dd class="mt-0.5 text-lg font-semibold tabular-nums">{{ kpi.value }}</dd>
          <dd class="text-muted-foreground truncate text-xs tabular-nums">{{ kpi.hint }}</dd>
        </div>
      </dl>

      <button
        type="button"
        class="text-muted-foreground hover:text-foreground flex items-center gap-1 text-xs"
        :aria-expanded="chartsOpen"
        data-testid="run-stats-toggle"
        @click="toggleCharts"
      >
        <component
          :is="chartsOpen ? ChevronDown : ChevronRight"
          class="size-3.5"
          aria-hidden="true"
        />
        {{ chartsOpen ? 'Hide charts' : 'Show charts' }}
      </button>

      <div v-if="chartsOpen" class="grid gap-3 lg:grid-cols-2" data-testid="run-stats-charts">
        <section class="border-border bg-card rounded-lg border p-3">
          <h3 class="text-sm font-medium">Runs per day</h3>
          <div v-if="!hasDays" class="text-muted-foreground grid h-56 place-items-center text-xs">
            No runs in this range.
          </div>
          <VChart
            v-else
            class="h-56 w-full cursor-pointer"
            :option="runsOption"
            autoresize
            data-testid="chart-runs-per-day"
            @click="onRunsClick"
          />
        </section>

        <section class="border-border bg-card rounded-lg border p-3">
          <h3 class="text-sm font-medium">Cost per day</h3>
          <div v-if="!hasDays" class="text-muted-foreground grid h-56 place-items-center text-xs">
            No runs in this range.
          </div>
          <VChart
            v-else
            class="h-56 w-full cursor-pointer"
            :option="costOption"
            autoresize
            data-testid="chart-cost-per-day"
            @click="onCostClick"
          />
        </section>

        <section
          class="border-border bg-card rounded-lg border p-3"
          :class="{ 'lg:col-span-2': stats.failureReasons === null }"
        >
          <div class="flex flex-wrap items-center justify-between gap-2">
            <h3 class="text-sm font-medium">Breakdown</h3>
            <div class="flex gap-1" role="group" aria-label="Group the breakdown by">
              <button
                v-for="grouping in groupings"
                :key="grouping.value"
                type="button"
                class="rounded px-2 py-0.5 text-xs"
                :class="
                  groupBy === grouping.value
                    ? 'bg-primary/10 text-primary font-medium'
                    : 'text-muted-foreground hover:bg-muted'
                "
                :aria-pressed="groupBy === grouping.value"
                :data-testid="`breakdown-by-${grouping.value}`"
                @click="groupBy = grouping.value"
              >
                {{ grouping.label }}
              </button>
            </div>
          </div>
          <p
            v-if="sortedGroups.length === 0"
            class="text-muted-foreground py-6 text-center text-xs"
          >
            No runs in this range.
          </p>
          <div v-else class="mt-2 max-h-64 overflow-auto">
            <table class="w-full text-xs" data-testid="breakdown-table">
              <thead class="text-muted-foreground">
                <tr class="border-border border-b">
                  <th class="py-1.5 pr-2 text-left font-normal">
                    {{ groupings.find((grouping) => grouping.value === groupBy)?.label }}
                  </th>
                  <th
                    v-for="column in columns"
                    :key="column.key"
                    class="px-2 py-1.5 text-right font-normal"
                  >
                    <button
                      type="button"
                      class="hover:text-foreground inline-flex items-center gap-0.5"
                      :aria-sort="
                        sortColumn === column.key
                          ? sortDescending
                            ? 'descending'
                            : 'ascending'
                          : 'none'
                      "
                      @click="sortBy(column.key)"
                    >
                      {{ column.label }}
                      <component
                        :is="sortDescending ? ArrowDown : ArrowUp"
                        v-if="sortColumn === column.key"
                        class="size-3"
                        aria-hidden="true"
                      />
                    </button>
                  </th>
                </tr>
              </thead>
              <tbody class="divide-border divide-y">
                <tr
                  v-for="group in sortedGroups"
                  :key="group.key ?? '-'"
                  :class="group.key === null ? '' : 'hover:bg-muted cursor-pointer'"
                  :title="group.key === null ? undefined : 'Show these runs'"
                  data-testid="breakdown-row"
                  @click="drillGroup(group)"
                >
                  <td class="max-w-48 truncate py-1.5 pr-2">{{ groupLabel(group) }}</td>
                  <td class="px-2 text-right tabular-nums">{{ group.runs }}</td>
                  <td class="px-2 text-right tabular-nums">
                    {{ formatPercent(group.succeeded, group.finished) }}
                  </td>
                  <td class="px-2 text-right tabular-nums">{{ formatUsd(group.costUsd) }}</td>
                  <td class="px-2 text-right tabular-nums">
                    {{ formatUsd(group.averageCostUsd) }}
                  </td>
                  <td class="px-2 text-right tabular-nums">
                    {{ formatSeconds(group.medianDurationSeconds) }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>

        <section
          v-if="stats.failureReasons !== null"
          class="border-border bg-card rounded-lg border p-3"
        >
          <h3 class="text-sm font-medium">Top failure reasons</h3>
          <p class="text-muted-foreground text-xs">Failed and timed-out runs.</p>
          <p
            v-if="stats.failureReasons.length === 0"
            class="text-muted-foreground py-6 text-center text-xs"
          >
            Nothing failed in this range.
          </p>
          <ul
            v-else
            class="divide-border mt-2 max-h-64 divide-y overflow-auto text-xs"
            data-testid="failure-reasons"
          >
            <li v-for="failure in stats.failureReasons" :key="failure.reason ?? '-'">
              <button
                v-if="failure.reason !== null"
                type="button"
                class="hover:bg-muted flex w-full items-center gap-2 px-1 py-1.5 text-left"
                title="Show these runs"
                @click="emit('drill', { failure: failure.reason })"
              >
                <span class="min-w-0 flex-1 truncate font-mono">{{ failure.reason }}</span>
                <span class="tabular-nums">{{ failure.runs }}</span>
              </button>
              <div v-else class="text-muted-foreground flex items-center gap-2 px-1 py-1.5">
                <span class="min-w-0 flex-1 italic">No reason given</span>
                <span class="tabular-nums">{{ failure.runs }}</span>
              </div>
            </li>
          </ul>
        </section>
      </div>
    </template>
  </section>
</template>
