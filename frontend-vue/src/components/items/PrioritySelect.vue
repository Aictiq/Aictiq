<script setup lang="ts">
import type { WorkItemPriority } from '@/api/items'
import PriorityIcon from '@/components/common/PriorityIcon.vue'
import { priorityOptions } from '@/lib/inline-edits'
import { cn } from '@/lib/utils'

/** The priority bars with a select beside them; see AssigneeSelect for `collapse`. */
const props = withDefaults(
  defineProps<{
    modelValue: WorkItemPriority
    label: string
    disabled?: boolean
    collapse?: boolean
    class?: string
  }>(),
  { disabled: false, collapse: false, class: undefined },
)
const emit = defineEmits<{ change: [priority: WorkItemPriority] }>()

function onChange(event: Event) {
  const value = (event.target as HTMLSelectElement).value as WorkItemPriority
  if (value !== props.modelValue) emit('change', value)
}
</script>

<template>
  <span
    :class="cn('relative inline-flex min-w-0 items-center gap-1.5', props.class)"
    @click.stop
    @mousedown.stop
    @keydown.stop
  >
    <PriorityIcon :priority="modelValue" class="flex-none" />
    <select
      :value="modelValue"
      :aria-label="label"
      :class="
        cn(
          'border-input bg-background min-w-0 flex-1 rounded border px-1 py-0.5 text-xs disabled:opacity-60',
          collapse && 'absolute inset-0 opacity-0 sm:static sm:opacity-100',
        )
      "
      :disabled="disabled"
      @change="onChange"
    >
      <option v-for="option in priorityOptions" :key="option.value" :value="option.value">
        {{ option.label }}
      </option>
    </select>
  </span>
</template>
