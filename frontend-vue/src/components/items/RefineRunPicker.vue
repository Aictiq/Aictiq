<script setup lang="ts">
import { computed } from 'vue'

import { harnessLabels, playbookHarnesses } from '@/api/playbooks'
import type { RunnerChoice } from '@/api/runners'
import { runnerCanRun } from '@/lib/runs'

/**
 * Where and with what a refine run goes: a harness, defaulting to the refine playbook's, and
 * with more than one runner, the runner - only those that can run the chosen harness.
 */
const props = defineProps<{
  runners: RunnerChoice[]
  disabled?: boolean
}>()

const runnerId = defineModel<string | null>('runnerId', { required: true })
const harness = defineModel<string | null>('harness', { required: true })

const compatibleRunners = computed(() =>
  props.runners.filter((runner) => runnerCanRun(runner, harness.value)),
)

/** One runner is no choice: every run goes to it anyway. */
const showRunners = computed(() => props.runners.length > 1)

const chosenRunner = computed(
  () => props.runners.find((runner) => runner.id === runnerId.value) ?? null,
)
</script>

<template>
  <div class="flex flex-wrap items-end gap-3" data-testid="refine-run-picker">
    <div class="space-y-1">
      <label for="refine-run-harness" class="text-muted-foreground block text-xs font-medium"
        >Harness</label
      >
      <select
        id="refine-run-harness"
        v-model="harness"
        :disabled="disabled"
        data-testid="refine-run-harness"
        class="border-input bg-background block rounded border px-2 py-1 text-sm"
      >
        <option v-for="option in playbookHarnesses" :key="option" :value="option">
          {{ harnessLabels[option] }}
        </option>
      </select>
    </div>
    <div v-if="showRunners" class="space-y-1">
      <label for="refine-run-runner" class="text-muted-foreground block text-xs font-medium"
        >Runner</label
      >
      <select
        id="refine-run-runner"
        v-model="runnerId"
        :disabled="disabled"
        data-testid="refine-run-runner"
        class="border-input bg-background block rounded border px-2 py-1 text-sm"
      >
        <option :value="null">Any free runner</option>
        <option v-for="runner in compatibleRunners" :key="runner.id" :value="runner.id">
          {{ runner.name }} · {{ runner.isOnline ? 'online' : 'offline' }}
        </option>
      </select>
    </div>
    <p
      v-if="showRunners && chosenRunner && !chosenRunner.isOnline"
      class="text-muted-foreground basis-full text-xs"
    >
      {{ chosenRunner.name }} is offline. The run waits in the queue until it comes back.
    </p>
  </div>
</template>
