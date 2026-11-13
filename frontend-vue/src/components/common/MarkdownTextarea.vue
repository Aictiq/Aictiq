<script setup lang="ts">
import { nextTick, ref } from 'vue'

import EmojiPicker from './EmojiPicker.vue'

defineOptions({ inheritAttrs: false })
const props = defineProps<{ modelValue: string; disabled?: boolean; readonly?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const textarea = ref<HTMLTextAreaElement | null>(null)
let selection = { from: 0, to: 0 }

function rememberSelection() {
  const input = textarea.value
  if (input) selection = { from: input.selectionStart, to: input.selectionEnd }
}

async function insertEmoji(emoji: string) {
  const input = textarea.value
  if (!input || props.disabled || props.readonly) return
  input.focus()
  input.setSelectionRange(selection.from, selection.to)
  // Native editing preserves the browser's undo stack, unlike assigning textarea.value.
  // setRangeText is the fallback for browsers without the native insertion command.
  if (!document.execCommand?.('insertText', false, emoji)) {
    input.setRangeText(emoji, selection.from, selection.to, 'end')
    emit('update:modelValue', input.value)
  }
  const caret = selection.from + emoji.length
  await nextTick()
  input.setSelectionRange(caret, caret)
}
</script>

<template>
  <div class="border-input bg-background rounded border">
    <div class="border-border border-b p-1">
      <EmojiPicker :disabled="disabled || readonly" @open="rememberSelection" @select="insertEmoji" />
    </div>
    <textarea
      ref="textarea"
      v-bind="$attrs"
      :value="modelValue"
      :disabled="disabled"
      :readonly="readonly"
      class="min-h-40 w-full rounded-b bg-transparent p-2 font-mono text-xs"
      @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)"
    />
  </div>
</template>
