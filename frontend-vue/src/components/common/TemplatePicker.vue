<script setup lang="ts">
import { computed } from 'vue'

import type { ItemTemplate, WorkItemType } from '@/api/templates'

const props = withDefaults(
  defineProps<{
    modelValue: string | null
    templates: ItemTemplate[]
    type: WorkItemType
    disabled?: boolean
  }>(),
  { disabled: false },
)

const emit = defineEmits<{ 'update:modelValue': [value: string | null]; select: [template: ItemTemplate | null] }>()

const choices = computed(() =>
  props.templates.filter((template) => template.type === props.type).sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.name.localeCompare(b.name)),
)

function choose(value: string) {
  const template = choices.value.find((candidate) => candidate.id === value) ?? null
  emit('update:modelValue', template?.id ?? null)
  emit('select', template)
}
</script>

<template>
  <label class="grid gap-1.5 text-sm">
    <span class="font-medium">Template</span>
    <select
      :value="modelValue ?? ''"
      :disabled="disabled"
      class="border-input bg-background h-9 rounded-md border px-2 text-sm"
      aria-label="Choose an item template"
      @change="choose(($event.target as HTMLSelectElement).value)"
    >
      <option value="">No template</option>
      <option v-for="template in choices" :key="template.id" :value="template.id">
        {{ template.name }}{{ template.isDefault ? ' (default)' : '' }}
      </option>
    </select>
  </label>
</template>
