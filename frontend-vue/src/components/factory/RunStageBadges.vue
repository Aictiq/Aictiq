<script setup lang="ts">
import { computed } from 'vue'

import type { Run } from '@/api/runs'
import { runStageLabels, stageVerdictLabel } from '@/lib/runs'

/**
 * Where a run sits in a playbook chain, on every run surface: a stage run's name and verdict,
 * or "Rework" on the implement run that addresses a stage's findings. Draws nothing for a run
 * outside a chain.
 */
const props = defineProps<{
  run: Pick<Run, 'kind' | 'playbookName' | 'stageVerdict' | 'reworkOfRunId'>
}>()

const labels = computed(() => runStageLabels(props.run))
</script>

<template>
  <template v-if="labels">
    <span
      v-if="labels.stage"
      class="border-border text-muted-foreground inline-flex shrink-0 items-center rounded-full border px-2 py-0.5 text-[11px] whitespace-nowrap"
      data-testid="run-stage-badge"
      >{{ labels.stage }}</span
    >
    <span
      v-if="labels.verdict"
      class="inline-flex shrink-0 items-center rounded-full border px-2 py-0.5 text-[11px] font-medium whitespace-nowrap"
      :class="
        labels.verdict === 'pass'
          ? 'border-success/35 bg-success/10 text-success'
          : 'border-warning/40 bg-warning/10 text-warning'
      "
      data-testid="run-stage-verdict"
      >{{ stageVerdictLabel[labels.verdict] }}</span
    >
    <span
      v-if="labels.rework"
      class="border-border text-muted-foreground inline-flex shrink-0 items-center rounded-full border px-2 py-0.5 text-[11px] whitespace-nowrap"
      data-testid="run-rework-badge"
      >Rework</span
    >
  </template>
</template>
