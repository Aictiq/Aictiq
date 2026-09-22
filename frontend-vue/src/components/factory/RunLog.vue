<script setup lang="ts">
import { useVirtualList } from '@vueuse/core'
import { ArrowDownToLine } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

import type { Run, RunLogStream } from '@/api/runs'
import { getRunLog } from '@/api/runs'
import { Button } from '@/components/ui/button'
import {
  applyLogPage,
  emptyLogState,
  hasMoreLogLines,
  isLiveRun,
  lastLogSeq,
  runWaitingMessage,
  type RunLogState,
} from '@/lib/runs'
import { factoryPath } from '@/router/paths'

/**
 * A run's raw output: the initial chunks from the log endpoint, then the hub's
 * `run.log` events (the page owns the connection and calls `tail`) nudging tail fetches
 * that always pass "everything up to here" as their cursor - so an event that arrives out
 * of order, or one that never arrives at all, heals itself on the next fetch instead of
 * showing a lie.
 *
 * The view is virtualised: a run can produce tens of thousands of lines, and the page
 * must not die rendering the ones nobody scrolled to. "Follow" sticks to the bottom the
 * way a terminal does; scrolling up is how you stop it.
 */
const props = defineProps<{ slug: string; run: Run }>()

const PAGE_SIZE = 500
const ROW_HEIGHT = 20
/** How far from the bottom the viewport may drift before following stops. */
const FOLLOW_SLACK = 80

const state = ref<RunLogState>({ ...emptyLogState })
const loading = ref(true)
const failed = ref(false)
const follow = ref(true)

const live = computed(() => isLiveRun(props.run.status))
const waiting = computed(() => runWaitingMessage(props.run))

interface LogRow {
  key: string
  stream: RunLogStream
  text: string
}

/** A stored chunk may hold several lines of output; the view scrolls by lines. */
const rows = computed<LogRow[]>(() =>
  state.value.lines.flatMap((line) =>
    line.text
      .split('\n')
      .map((text, index) => ({ key: `${line.seq}:${index}`, stream: line.stream, text })),
  ),
)

const { list: virtualList, containerProps, wrapperProps } = useVirtualList(rows, {
  itemHeight: ROW_HEIGHT,
  overscan: 12,
})
const containerElement = computed(() => containerProps.ref.value)

async function fetchTail(): Promise<void> {
  const runId = props.run.id
  let guard = 0
  // A tail that fills a full page may have more behind it; keep going until it doesn't.
  while (guard++ < 20) {
    const page = await getRunLog(props.slug, runId, lastLogSeq(state.value), PAGE_SIZE)
    // The page moved on to another run while this was in flight.
    if (props.run.id !== runId) return
    state.value = applyLogPage(state.value, page)
    if (!hasMoreLogLines(page, PAGE_SIZE)) return
  }
}

let fetching = false
/** A nudge that arrived mid-fetch: its lines may be past the cursor that fetch used. */
let again = false
async function drain() {
  if (fetching) {
    again = true
    return
  }
  fetching = true
  try {
    do {
      again = false
      await fetchTail()
    } while (again)
    failed.value = false
  } catch {
    failed.value = true
  } finally {
    fetching = false
    if (follow.value) void nextTick(scrollToBottom)
  }
}

/** Coalesce a burst of `run.log` events into one fetch. */
let tailTimer: ReturnType<typeof setTimeout> | undefined
function tail() {
  if (!live.value) return
  clearTimeout(tailTimer)
  tailTimer = setTimeout(() => void drain(), 75)
}

function scrollToBottom() {
  const element = containerElement.value
  if (element) element.scrollTop = element.scrollHeight
}

function onScroll() {
  containerProps.onScroll()
  const element = containerElement.value
  if (!element) return
  if (element.scrollHeight - element.scrollTop - element.clientHeight > FOLLOW_SLACK) {
    follow.value = false
  }
}

function resumeFollow() {
  follow.value = true
  void nextTick(scrollToBottom)
}

function reset() {
  clearTimeout(tailTimer)
  again = false
  state.value = { ...emptyLogState }
  follow.value = true
  failed.value = false
  loading.value = true
}

// Two sources, not one getter returning a fresh array: that array would read as "changed"
// on every refetch of the run and throw the log away each time its status moved.
watch(
  [() => props.slug, () => props.run.id],
  async () => {
    reset()
    try {
      await drain()
    } finally {
      loading.value = false
      if (follow.value) void nextTick(scrollToBottom)
    }
  },
  { immediate: true },
)

// The run finishing publishes its last log first; the run's own refetch lags the hub by
// a moment, so the transition to terminal gets one final catch-up regardless of order.
watch(
  () => props.run.status,
  (status, previous) => {
    if (isLiveRun(previous) && !isLiveRun(status)) void drain()
  },
)

// Realtime is an enhancement, never the data source: a hub that will not connect still
// leaves a log that keeps up, a little more slowly.
let poll: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  poll = setInterval(() => {
    if (live.value && document.visibilityState !== 'hidden') tail()
  }, 30_000)
})
onBeforeUnmount(() => {
  clearTimeout(tailTimer)
  clearInterval(poll)
})

defineExpose({
  tail,
  flush: () => void drain(),
})
</script>

<template>
  <div class="flex min-h-0 flex-1 flex-col">
    <div class="flex flex-wrap items-center gap-2 pb-2">
      <p class="font-label">Log</p>
      <span class="text-muted-foreground text-xs">{{ rows.length }} lines</span>
      <Button
        v-if="live"
        variant="ghost"
        size="sm"
        class="ml-auto"
        :class="!follow && 'text-muted-foreground'"
        :title="follow ? 'Following the bottom of the log' : 'Scrolling up paused following'"
        data-testid="run-log-follow"
        @click="resumeFollow"
      >
        <ArrowDownToLine class="size-3.5" aria-hidden="true" />
        {{ follow ? 'Following' : 'Jump to latest' }}
      </Button>
    </div>

    <p
      v-if="props.run.cancelRequested && live"
      class="border-warning/40 bg-warning/10 mb-2 rounded border px-2.5 py-1.5 text-xs"
      data-testid="run-log-cancelling"
    >
      Cancellation requested - the runner stops at its next check.
    </p>
    <p v-if="state.truncated" class="border-warning/40 bg-warning/10 mb-2 rounded border px-2.5 py-1.5 text-xs" data-testid="run-log-truncated">
      This log reached its size cap, so its earliest lines are gone. What is shown here is
      everything that survived.
    </p>

    <div
      v-if="waiting"
      class="border-border text-muted-foreground mb-2 flex flex-wrap items-center gap-1 rounded border px-2.5 py-1.5 text-xs"
      data-testid="run-log-waiting"
    >
      {{ waiting }}
      <RouterLink
        :to="factoryPath(props.slug, 'runners')"
        class="text-primary underline underline-offset-2"
        >Runners</RouterLink
      >
    </div>

    <p v-if="failed" class="text-destructive mb-2 text-xs">
      The log could not be refreshed - it will try again.
    </p>

    <div
      v-if="loading"
      class="border-border text-muted-foreground flex-1 rounded border p-3 text-xs"
    >
      Loading log…
    </div>
    <div
      v-else-if="rows.length === 0"
      class="border-border text-muted-foreground flex-1 rounded border p-3 text-xs"
    >
      {{ waiting ? 'Nothing yet.' : 'The run wrote nothing to its log.' }}
    </div>
    <div
      v-else
      v-bind="{ ...containerProps, onScroll }"
      class="bg-background border-border overflow-x-auto rounded border font-mono text-xs leading-5"
      :style="{ height: '60vh', minHeight: '18rem' }"
      data-testid="run-log"
    >
      <div v-bind="wrapperProps">
        <div
          v-for="row in virtualList"
          :key="row.data.key"
          :style="{ height: `${ROW_HEIGHT}px` }"
          class="whitespace-pre px-2.5"
          :class="
            row.data.stream === 'stderr'
              ? 'text-destructive/90'
              : row.data.stream === 'event'
                ? 'text-primary/80 italic'
                : 'text-foreground/90'
          "
          >{{ row.data.text }}</div
        >
      </div>
    </div>
  </div>
</template>
