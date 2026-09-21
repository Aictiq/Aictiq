<script setup lang="ts">
import { ref } from 'vue'
import { X } from '@lucide/vue'

import KeyChip from '@/components/common/KeyChip.vue'
import { useFocusTrap } from '@/composables/useFocusTrap'
import { useShortcut } from '@/composables/useShortcuts'

/**
 * The `?` overlay. A keyboard-first app that never tells you its keys is a keyboard-first
 * app for the person who wrote it, so this is part of the shell rather than a docs page.
 */
const open = ref(false)
const dialog = ref<HTMLElement | null>(null)

useShortcut('shift+/', () => (open.value = !open.value))
useShortcut('escape', () => (open.value = false), { allowInInput: true, when: () => open.value })
useFocusTrap(dialog, open)

const groups = [
  {
    label: 'General',
    shortcuts: [
      { keys: 'mod+k', description: 'Open the command palette' },
      { keys: 'shift+/', description: 'Show this help' },
      { keys: 'escape', description: 'Close the palette or this help' },
    ],
  },
  {
    label: 'Navigation',
    shortcuts: [
      { keys: 'g then m', description: 'Go to My work' },
      { keys: 'g then i', description: 'Go to Items' },
      { keys: 'g then b', description: 'Go to Board' },
    ],
  },
  {
    label: 'Appearance',
    shortcuts: [
      { keys: 'mod+shift+l', description: 'Cycle theme (light, dark, system)' },
      { keys: 'mod+b', description: 'Collapse or expand the sidebar' },
    ],
  },
]
</script>

<template>
  <div
    v-if="open"
    class="fixed inset-0 z-60 flex items-center justify-center bg-black/60 backdrop-blur-[3px]"
    @click="open = false"
  >
    <div
      ref="dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="shortcut-help-title"
      tabindex="-1"
      class="bg-popover border-border w-[520px] max-w-[92vw] overflow-hidden rounded-lg border shadow-2xl"
      @click.stop
    >
      <div class="border-border flex items-center justify-between border-b px-4 py-3">
        <h2 id="shortcut-help-title" class="text-sm font-semibold">Keyboard shortcuts</h2>
        <div class="flex items-center gap-2">
          <KeyChip label="ESC" />
          <button data-autofocus type="button" class="rounded p-1 hover:bg-accent" aria-label="Close keyboard shortcuts" @click="open = false">
            <X class="size-4" aria-hidden="true" />
          </button>
        </div>
      </div>
      <div class="max-h-[60vh] space-y-4 overflow-y-auto p-4">
        <section v-for="group in groups" :key="group.label">
          <h3 class="font-label pb-1.5">{{ group.label }}</h3>
          <dl class="space-y-1">
            <div
              v-for="shortcut in group.shortcuts"
              :key="shortcut.keys"
              class="flex items-center justify-between gap-4 text-[13px]"
            >
              <dt class="text-muted-foreground">{{ shortcut.description }}</dt>
              <dd><KeyChip :binding="shortcut.keys" /></dd>
            </div>
          </dl>
        </section>
      </div>
    </div>
  </div>
</template>
