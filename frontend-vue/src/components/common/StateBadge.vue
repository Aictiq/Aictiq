<script setup lang="ts">
import { computed } from 'vue'

import { cn } from '@/lib/utils'

/**
 * A workflow state, coloured by its *category* rather than its name. Projects configure
 * their own state names, so nothing may key off "In progress" — the category is
 * the stable part, and it is what the analytics and board code reads too.
 */
export type StateCategory = 'proposed' | 'active' | 'resolved' | 'completed' | 'removed'

const props = withDefaults(
  defineProps<{ name: string; category: StateCategory; class?: string }>(),
  { class: undefined },
)

const tones = {
  proposed: 'text-muted-foreground border-border',
  active: 'text-primary border-primary/35 bg-primary/10',
  resolved: 'text-info border-info/35 bg-info/10',
  completed: 'text-success border-success/35 bg-success/10',
  removed: 'text-muted-foreground border-border line-through',
} as const

const tone = computed(() => tones[props.category])
</script>

<template>
  <span
    :class="
      cn(
        'inline-flex items-center gap-1.5 rounded-sm border px-1.5 py-0.5 text-[11px] leading-none',
        tone,
        props.class,
      )
    "
  >
    <span class="size-1.5 rounded-full bg-current" aria-hidden="true" />
    {{ name }}
  </span>
</template>
