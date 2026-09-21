<script setup lang="ts">
import { computed } from 'vue'
import {
  Activity,
  BarChart3,
  CalendarClock,
  CheckCircle2,
  FileText,
  LayoutPanelTop,
  ListTodo,
  Sparkles,
  UsersRound,
} from '@lucide/vue'

const props = defineProps<{ type: string; data: unknown; config: Record<string, unknown> }>()

type CountRow = { stateId?: string | null; stateName?: string | null; assigneeId?: string | null; count?: number }
type ActivityRow = { itemId?: string; at?: string; field?: string }

const title = computed(() => ({
  velocity: 'Team velocity', burndown: 'Sprint burndown', cfd: 'Flow of work', 'cycle-time': 'Cycle time',
  'sprint-health': 'Sprint health', 'items-by-state': 'Work by state', 'items-by-assignee': 'Work by assignee',
  'recent-activity': 'Recent activity', 'saved-view-count': 'Saved views', markdown: 'Project note',
}[props.type] ?? props.type.replaceAll('-', ' ')))

const icon = computed(() => ({
  velocity: BarChart3, burndown: CalendarClock, cfd: Activity, 'cycle-time': CalendarClock,
  'sprint-health': CheckCircle2, 'items-by-state': ListTodo, 'items-by-assignee': UsersRound,
  'recent-activity': Activity, 'saved-view-count': LayoutPanelTop, markdown: FileText,
}[props.type] ?? Sparkles))

const rows = computed<CountRow[]>(() => Array.isArray(props.data) ? props.data as CountRow[] : [])
const activity = computed<ActivityRow[]>(() => Array.isArray(props.data) ? props.data as ActivityRow[] : [])
const total = computed(() => rows.value.reduce((sum, row) => sum + (Number(row.count) || 0), 0))
const savedViews = computed(() => {
  const value = props.data as { count?: unknown } | null
  return typeof value?.count === 'number' ? value.count : 0
})
const note = computed(() => {
  const data = props.data as { markdown?: unknown } | null
  return String(data?.markdown ?? props.config.markdown ?? '')
})

function label(row: CountRow, index: number) {
  const value = props.type === 'items-by-state' ? row.stateName ?? row.stateId : row.assigneeId
  if (!value) return props.type === 'items-by-assignee' ? 'Unassigned' : 'No state'
  return props.type === 'items-by-assignee' ? `Member ${index + 1}` : String(value).replaceAll('-', ' ')
}

const maxCount = computed(() => Math.max(...rows.value.map(row => Number(row.count) || 0), 1))
function width(count?: number) { return `${Math.max(4, ((Number(count) || 0) / maxCount.value) * 100)}%` }

function relativeTime(value?: string) {
  if (!value) return 'Recently'
  const minutes = Math.round((Date.now() - new Date(value).getTime()) / 60_000)
  if (minutes < 1) return 'Just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.round(hours / 24)}d ago`
}
</script>

<template>
  <div class="dashboard-widget">
    <header class="dashboard-widget__header">
      <div class="dashboard-widget__heading">
        <component :is="icon" class="dashboard-widget__icon" aria-hidden="true" />
        <h2>{{ title }}</h2>
      </div>
      <span v-if="type === 'items-by-state' || type === 'items-by-assignee'" class="dashboard-widget__total">{{ total }} total</span>
    </header>

    <template v-if="type === 'items-by-state' || type === 'items-by-assignee'">
      <div v-if="rows.length" class="dashboard-widget__distribution">
        <div v-for="(row, index) in rows.slice(0, 6)" :key="`${label(row, index)}-${index}`" class="dashboard-widget__bar-row">
          <span :title="label(row, index)">{{ label(row, index) }}</span>
          <div class="dashboard-widget__bar-track"><i :style="{ width: width(row.count) }" /></div>
          <b>{{ row.count ?? 0 }}</b>
        </div>
      </div>
      <p v-else class="dashboard-widget__empty">No work items to show yet.</p>
    </template>

    <template v-else-if="type === 'recent-activity'">
      <ol v-if="activity.length" class="dashboard-widget__activity">
        <li v-for="entry in activity.slice(0, 4)" :key="`${entry.itemId}-${entry.at}-${entry.field}`">
          <span class="dashboard-widget__activity-dot" />
          <div><p>{{ entry.field || 'Updated work item' }}</p><span>{{ relativeTime(entry.at) }}</span></div>
        </li>
      </ol>
      <p v-else class="dashboard-widget__empty">Activity will appear as your team works.</p>
    </template>

    <template v-else-if="type === 'saved-view-count'">
      <div class="dashboard-widget__metric"><strong>{{ savedViews }}</strong><span>Reusable team views</span></div>
      <p class="dashboard-widget__caption">Keep frequently used filters close at hand.</p>
    </template>

    <template v-else-if="type === 'markdown'">
      <p v-if="note" class="dashboard-widget__note">{{ note }}</p>
      <p v-else class="dashboard-widget__empty">Add a note in edit mode to give this dashboard context.</p>
    </template>

    <template v-else>
      <div class="dashboard-widget__coming">
        <span class="dashboard-widget__pulse" />
        <div><strong>Ready for data</strong><p>{{ type === 'sprint-health' ? 'Start a sprint to see its delivery health here.' : 'This view will populate as project data becomes available.' }}</p></div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.dashboard-widget { height: 100%; display: flex; min-height: 0; flex-direction: column; }
.dashboard-widget__header { display: flex; align-items: center; justify-content: space-between; gap: .75rem; }
.dashboard-widget__heading { display: flex; min-width: 0; align-items: center; gap: .5rem; }
.dashboard-widget__heading h2 { overflow: hidden; color: var(--foreground); font-size: .8rem; font-weight: 600; letter-spacing: -.01em; text-overflow: ellipsis; text-transform: capitalize; white-space: nowrap; }
.dashboard-widget__icon { color: var(--primary); width: 1rem; height: 1rem; }
.dashboard-widget__total { color: var(--muted-foreground); font-family: 'JetBrains Mono Variable', monospace; font-size: .6rem; letter-spacing: .04em; white-space: nowrap; }
.dashboard-widget__distribution { display: grid; flex: 1; gap: .52rem; justify-content: center; margin-top: .85rem; overflow: auto; }
.dashboard-widget__bar-row { align-items: center; display: grid; gap: .5rem; grid-template-columns: minmax(3.7rem, .9fr) minmax(3rem, 1.35fr) 1.2rem; }
.dashboard-widget__bar-row > span { color: var(--muted-foreground); font-size: .68rem; overflow: hidden; text-overflow: ellipsis; text-transform: capitalize; white-space: nowrap; }
.dashboard-widget__bar-track { background: color-mix(in oklab, var(--muted) 82%, transparent); border-radius: 999px; height: .42rem; overflow: hidden; }
.dashboard-widget__bar-track i { background: linear-gradient(90deg, color-mix(in oklab, var(--primary) 68%, white), var(--primary)); border-radius: inherit; display: block; height: 100%; transition: width .35s ease; }
.dashboard-widget__bar-row b { color: var(--foreground); font-family: 'JetBrains Mono Variable', monospace; font-size: .65rem; font-weight: 500; text-align: right; }
.dashboard-widget__empty { display: grid; min-height: 100px; flex: 1; place-items: center; color: var(--muted-foreground); font-size: .75rem; text-align: center; }
.dashboard-widget__activity { margin-top: .75rem; overflow: auto; }
.dashboard-widget__activity li { display: flex; gap: .65rem; padding-bottom: .7rem; position: relative; }
.dashboard-widget__activity li:not(:last-child)::before { background: var(--border); bottom: 0; content: ''; left: 4px; position: absolute; top: 12px; width: 1px; }
.dashboard-widget__activity-dot { background: var(--primary); border-radius: 999px; height: 9px; margin-top: .25rem; min-width: 9px; position: relative; z-index: 1; }
.dashboard-widget__activity p { color: var(--foreground); font-size: .75rem; line-height: 1.2; }
.dashboard-widget__activity span:not(.dashboard-widget__activity-dot) { color: var(--muted-foreground); font-family: 'JetBrains Mono Variable', monospace; font-size: .6rem; }
.dashboard-widget__metric { display: flex; flex: 1; flex-direction: column; justify-content: center; margin-top: .5rem; }
.dashboard-widget__metric strong { color: var(--primary); font-size: clamp(2.2rem, 4vw, 3.4rem); font-weight: 600; letter-spacing: -.06em; line-height: 1; }
.dashboard-widget__metric span, .dashboard-widget__caption { color: var(--muted-foreground); font-size: .7rem; margin-top: .45rem; }
.dashboard-widget__caption { border-top: 1px solid var(--border); padding-top: .6rem; }
.dashboard-widget__note { color: var(--foreground); font-size: .8rem; line-height: 1.55; margin-top: .9rem; overflow: auto; white-space: pre-wrap; }
.dashboard-widget__coming { align-items: center; display: flex; flex: 1; gap: .7rem; justify-content: center; text-align: left; }
.dashboard-widget__coming strong { display: block; font-size: .78rem; font-weight: 600; }.dashboard-widget__coming p { color: var(--muted-foreground); font-size: .7rem; line-height: 1.4; margin-top: .15rem; max-width: 18rem; }
.dashboard-widget__pulse { background: color-mix(in oklab, var(--primary) 18%, transparent); border: 1px solid color-mix(in oklab, var(--primary) 60%, transparent); border-radius: 999px; box-shadow: 0 0 0 5px color-mix(in oklab, var(--primary) 8%, transparent); height: .55rem; min-width: .55rem; }
</style>
