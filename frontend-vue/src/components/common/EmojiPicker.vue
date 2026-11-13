<script setup lang="ts">
import { Smile } from '@lucide/vue'
import { PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from 'reka-ui'
import { ref, watch } from 'vue'

import { emojis } from './emojis'

const props = defineProps<{ disabled?: boolean }>()
const emit = defineEmits<{ open: []; select: [emoji: string] }>()
const open = ref(false)
let selected = false

function changeOpen(value: boolean) {
  open.value = value && !props.disabled
  if (open.value) {
    selected = false
    emit('open')
  }
}

function pick(emoji: string) {
  if (props.disabled) return
  selected = true
  open.value = false
  emit('select', emoji)
}

// Reka handles Escape, outside clicks, Tab and restoring trigger focus on dismissal.
// After a choice, the consumer restores the saved selection and focuses the editor.
function closeAutoFocus(event: Event) {
  if (selected) event.preventDefault()
}

function navigate(event: KeyboardEvent) {
  const steps: Record<string, number> = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: 6, ArrowUp: -6 }
  const buttons = Array.from((event.currentTarget as HTMLElement).querySelectorAll('button'))
  const index = buttons.indexOf(event.target as HTMLButtonElement)
  let next: number
  if (event.key === 'Home') next = 0
  else if (event.key === 'End') next = buttons.length - 1
  else if (event.key in steps) next = (index + steps[event.key]! + buttons.length) % buttons.length
  else return
  event.preventDefault()
  buttons[next]?.focus()
}

watch(() => props.disabled, (disabled) => { if (disabled) open.value = false })
</script>

<template>
  <PopoverRoot :open="open" @update:open="changeOpen">
    <PopoverTrigger
      type="button"
      class="text-muted-foreground hover:text-foreground hover:bg-accent inline-flex size-7 shrink-0 items-center justify-center rounded disabled:opacity-50"
      aria-label="Insert emoji"
      title="Insert emoji"
      :disabled="disabled"
    >
      <Smile class="size-4" aria-hidden="true" />
    </PopoverTrigger>
    <PopoverPortal>
      <PopoverContent
        align="start"
        :side-offset="4"
        class="bg-popover text-popover-foreground border-border z-50 w-60 max-w-[calc(100vw-1rem)] rounded-md border p-2 shadow-md"
        @close-auto-focus="closeAutoFocus"
        @escape-key-down.stop
      >
        <p class="text-muted-foreground mb-1 px-1 text-xs">Insert emoji</p>
        <div class="grid grid-cols-6 gap-1" @keydown="navigate">
          <button
            v-for="emoji in emojis"
            :key="emoji.name"
            type="button"
            class="hover:bg-accent focus-visible:bg-accent focus-visible:ring-ring flex size-8 items-center justify-center rounded text-lg outline-none focus-visible:ring-2"
            :aria-label="emoji.name"
            :title="emoji.name"
            :disabled="disabled"
            @click="pick(emoji.text)"
          >{{ emoji.text }}</button>
        </div>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>
