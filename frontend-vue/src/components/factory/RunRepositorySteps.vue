<script setup lang="ts">
import { ExternalLink } from '@lucide/vue'
import { RouterLink } from 'vue-router'

import type { RunRepositoryStep, RunRepositoryStepState } from '@/api/runs'
import { pullRequestLabel } from '@/lib/repositories'

/** A multi-repository run's steps in order, each with its state, its run and its pull requests. */
defineProps<{
  slug: string
  steps: RunRepositoryStep[]
  /** The run being shown, highlighted among the steps. */
  currentRunId: string
}>()

const stateLabels: Record<RunRepositoryStepState, string> = {
  pending: 'Pending',
  waiting: 'Waiting',
  'waiting-for-merge': 'Waiting for merge',
  queued: 'Queued',
  running: 'Running',
  succeeded: 'Succeeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
  dropped: 'Dropped',
}

const stateClasses: Record<RunRepositoryStepState, string> = {
  pending: 'bg-muted text-muted-foreground',
  waiting: 'bg-muted text-muted-foreground',
  'waiting-for-merge': 'bg-amber-500/15 text-amber-700 dark:text-amber-300',
  queued: 'bg-muted text-foreground',
  running: 'bg-primary/15 text-primary',
  succeeded: 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-300',
  failed: 'bg-destructive/15 text-destructive',
  cancelled: 'bg-muted text-muted-foreground',
  dropped: 'bg-destructive/10 text-destructive',
}
</script>

<template>
  <section class="border-border rounded-lg border" data-testid="run-repository-steps">
    <header class="px-4 pt-3 pb-2">
      <h2 class="text-sm font-medium">Repository steps</h2>
    </header>
    <ol class="divide-border divide-y border-t">
      <li
        v-for="(step, index) in steps"
        :key="index"
        class="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2 text-xs"
        :class="{ 'bg-muted/60': step.runId === currentRunId }"
        data-testid="run-repository-step"
      >
        <span class="text-muted-foreground w-12 shrink-0">Step {{ index + 1 }}</span>
        <span class="font-mono font-medium">{{ step.aliases.join(', ') }}</span>
        <span
          class="rounded px-1.5 py-0.5 text-[11px] font-medium"
          :class="stateClasses[step.state]"
          data-testid="run-repository-step-state"
        >
          {{ stateLabels[step.state] ?? step.state }}
        </span>
        <span v-if="step.after.length > 0" class="text-muted-foreground">
          after {{ step.after.join(', ') }}{{ step.waitForMerge ? ' is merged' : '' }}
        </span>
        <span class="ml-auto flex flex-wrap items-center gap-3">
          <a
            v-for="url in step.pullRequestUrls"
            :key="url"
            :href="url"
            target="_blank"
            rel="noreferrer"
            class="text-primary inline-flex items-center gap-1 underline underline-offset-2"
          >
            <ExternalLink class="size-3" aria-hidden="true" />
            {{ pullRequestLabel(url) }}
          </a>
          <RouterLink
            v-if="step.runId && step.runId !== currentRunId"
            :to="`/o/${slug}/factory/runs/${step.runId}`"
            class="text-primary underline underline-offset-2"
          >
            Open run
          </RouterLink>
          <span v-else-if="step.runId" class="text-muted-foreground">This run</span>
        </span>
      </li>
    </ol>
  </section>
</template>
