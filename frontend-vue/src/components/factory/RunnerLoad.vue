<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'

import type { RunnerLoadEntry } from '@/api/runners'
import { loadBarClass, loadLabel, loadLevel, loadPercent, waitingSummary } from '@/lib/runners'
import { factoryPath, factoryRunPath } from '@/router/paths'

/**
 * One runner's load in the roster: its slots as a bar drawn like the usage meters, the runs it
 * holds one click away, and what waits for it. A runner that cannot work right now (offline,
 * disabled, never seen) shows a muted bar but still says what waits for it.
 */
const props = defineProps<{
  slug: string
  runnerName: string
  /** The runner's `--parallel` slots; 1 until it has reported them. */
  slots: number
  entry: RunnerLoadEntry
  muted: boolean
}>()

/** Enough links to see what it holds at a glance; the rest are one filter away. */
const shownRuns = 3

const level = computed(() => loadLevel(props.entry.running, props.slots))
const waiting = computed(() =>
  waitingSummary(props.entry.queued, props.entry.scheduled, props.entry.nextScheduledFor),
)
const runsPath = computed(
  () => `${factoryPath(props.slug, 'runs')}?runner=${encodeURIComponent(props.entry.runnerId)}`,
)
</script>

<template>
  <div
    class="flex flex-wrap items-center gap-x-3 gap-y-0.5 text-[11px]"
    data-testid="runner-load"
    :data-level="level"
  >
    <span class="text-muted-foreground w-14 flex-none">load</span>
    <span class="flex items-center gap-1.5">
      <span
        class="bg-muted h-1.5 w-16 overflow-hidden rounded-full"
        role="meter"
        :aria-label="`Slots in use on ${runnerName}`"
        aria-valuemin="0"
        :aria-valuemax="slots"
        :aria-valuenow="entry.running"
        :aria-valuetext="loadLabel(entry.running, slots)"
      >
        <span
          class="block h-full rounded-full"
          :class="loadBarClass(level, muted)"
          :style="{ width: `${loadPercent(entry.running, slots)}%` }"
        />
      </span>
      <span class="tabular-nums" :class="muted || level === 'idle' ? 'text-muted-foreground' : ''">
        {{ loadLabel(entry.running, slots) }}
      </span>
    </span>
    <span v-if="entry.active.length" class="flex flex-wrap items-center gap-x-1.5">
      <RouterLink
        v-for="run in entry.active.slice(0, shownRuns)"
        :key="run.id"
        :to="factoryRunPath(slug, run.id)"
        class="font-mono underline-offset-2 hover:underline"
        :title="run.playbookName ? `${run.itemKey} · ${run.playbookName}` : run.itemKey"
        :data-testid="`runner-active-run-${run.id}`"
      >
        {{ run.itemKey }}
      </RouterLink>
      <RouterLink
        v-if="entry.active.length > shownRuns"
        :to="runsPath"
        class="text-muted-foreground underline-offset-2 hover:underline"
      >
        +{{ entry.active.length - shownRuns }} more
      </RouterLink>
    </span>
    <span v-if="waiting" class="text-muted-foreground" data-testid="runner-waiting">
      {{ waiting }}
    </span>
  </div>
</template>
