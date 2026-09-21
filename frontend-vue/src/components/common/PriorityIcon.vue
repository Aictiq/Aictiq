<script setup lang="ts">
import { computed } from 'vue'

import { cn } from '@/lib/utils'

/**
 * Priority as bar height, the way tools that show hundreds of rows do it: readable at a
 * glance in a dense list, and — unlike colour alone — legible to someone who cannot
 * distinguish red from amber. The title and aria-label carry the name regardless.
 */
export type Priority = 'urgent' | 'high' | 'medium' | 'low' | 'none'

const props = withDefaults(defineProps<{ priority: Priority; class?: string }>(), {
  class: undefined,
})

const config = {
  urgent: { bars: 3, tone: 'bg-destructive', label: 'Urgent' },
  high: { bars: 3, tone: 'bg-warning', label: 'High' },
  medium: { bars: 2, tone: 'bg-muted-foreground', label: 'Medium' },
  low: { bars: 1, tone: 'bg-muted-foreground', label: 'Low' },
  none: { bars: 0, tone: 'bg-muted-foreground', label: 'No priority' },
} as const

const current = computed(() => config[props.priority])
</script>

<template>
  <span
    :class="cn('inline-flex h-3 items-end gap-px', props.class)"
    :title="current.label"
    role="img"
    :aria-label="`Priority: ${current.label}`"
  >
    <span
      v-for="bar in 3"
      :key="bar"
      class="w-[3px] rounded-[1px]"
      :class="[
        bar <= current.bars ? current.tone : 'bg-border',
        bar === 1 ? 'h-1' : bar === 2 ? 'h-2' : 'h-3',
      ]"
    />
  </span>
</template>
