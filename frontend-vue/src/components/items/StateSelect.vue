<script setup lang="ts">
import { computed } from 'vue'

import type { WorkflowState } from '@/api/workflows'
import { cn } from '@/lib/utils'

/**
 * "Move to" for a row: the item's workflow state as a select of only the states it may move
 * to (see `allowedStates`). The dot carries the state's category colour, the same signal
 * StateBadge gives; see AssigneeSelect for `collapse`.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string
    states: WorkflowState[]
    /** Shown while the workflow is still loading, so the row is never blank. */
    fallbackName: string
    label: string
    disabled?: boolean
    collapse?: boolean
    class?: string
  }>(),
  { disabled: false, collapse: false, class: undefined },
)
const emit = defineEmits<{ change: [stateId: string] }>()

const tones = {
  proposed: 'text-muted-foreground',
  active: 'text-primary',
  resolved: 'text-info',
  completed: 'text-success',
  removed: 'text-muted-foreground',
} as const
const current = computed(() => props.states.find((state) => state.id === props.modelValue))
const tone = computed(() => (current.value ? tones[current.value.category] : tones.proposed))

function onChange(event: Event) {
  const value = (event.target as HTMLSelectElement).value
  if (value && value !== props.modelValue) emit('change', value)
}
</script>

<template>
  <span
    :class="cn('relative inline-flex min-w-0 items-center gap-1.5', props.class)"
    @click.stop
    @mousedown.stop
    @keydown.stop
  >
    <span :class="cn('size-2 flex-none rounded-full bg-current', tone)" aria-hidden="true" />
    <select
      :value="modelValue"
      :aria-label="label"
      :title="current?.name ?? fallbackName"
      :class="
        cn(
          'border-input bg-background max-w-32 min-w-0 flex-1 rounded border px-1 py-0.5 text-xs disabled:opacity-60',
          collapse && 'absolute inset-0 opacity-0 sm:static sm:opacity-100',
        )
      "
      :disabled="disabled || !states.length"
      @change="onChange"
    >
      <option v-if="!current" :value="modelValue">{{ fallbackName }}</option>
      <option v-for="state in states" :key="state.id" :value="state.id">{{ state.name }}</option>
    </select>
  </span>
</template>
