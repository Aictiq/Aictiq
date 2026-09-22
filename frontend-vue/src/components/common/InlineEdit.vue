<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'

/**
 * Click to edit in place: Enter commits, Escape reverts, blur commits.
 *
 * Reverting on Escape matters more than it looks - inline edits sit in dense lists where
 * it is easy to start typing in the wrong row, and there must be a way out that does not
 * save. The commit only fires when the value actually changed, so a click-in/click-out
 * does not spend a request or a version bump.
 */
const props = withDefaults(
  defineProps<{ modelValue: string; placeholder?: string; disabled?: boolean; label?: string }>(),
  { placeholder: '', disabled: false, label: undefined },
)

const emit = defineEmits<{ 'update:modelValue': [string]; commit: [string] }>()

const editing = ref(false)
const draft = ref(props.modelValue)
const input = ref<HTMLInputElement | null>(null)

watch(
  () => props.modelValue,
  (value) => {
    if (!editing.value) draft.value = value
  },
)

async function start() {
  if (props.disabled) return
  draft.value = props.modelValue
  editing.value = true
  await nextTick()
  input.value?.focus()
  input.value?.select()
}

function commit() {
  if (!editing.value) return
  editing.value = false

  const next = draft.value.trim()
  if (next === props.modelValue || next.length === 0) {
    draft.value = props.modelValue
    return
  }

  emit('update:modelValue', next)
  emit('commit', next)
}

function cancel() {
  draft.value = props.modelValue
  editing.value = false
}
</script>

<template>
  <input
    v-if="editing"
    ref="input"
    v-model="draft"
    class="border-input bg-background focus-visible:ring-ring w-full rounded-sm border px-1.5 py-0.5 text-sm focus-visible:ring-2 focus-visible:outline-none"
    :placeholder="placeholder"
    :aria-label="label"
    @keydown.enter.prevent="commit"
    @keydown.esc.prevent="cancel"
    @blur="commit"
  />
  <button
    v-else
    type="button"
    class="hover:bg-accent w-full truncate rounded-sm px-1.5 py-0.5 text-left text-sm disabled:cursor-default disabled:hover:bg-transparent"
    :disabled="disabled"
    :aria-label="label ? `${label}: ${modelValue}. Activate to edit.` : undefined"
    @click="start"
  >
    <span v-if="modelValue">{{ modelValue }}</span>
    <span v-else class="text-muted-foreground">{{ placeholder }}</span>
  </button>
</template>
