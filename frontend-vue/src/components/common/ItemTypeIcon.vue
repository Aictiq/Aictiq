<script setup lang="ts">
import { Bookmark, Bug, Crown, ListChecks, Trophy } from '@lucide/vue'
import { computed } from 'vue'

import type { WorkItemType } from '@/api/items'
import { typeLabels } from '@/lib/hierarchy'
import { cn } from '@/lib/utils'

/** The work-item type as a glyph, so a mixed backlog reads at a glance. */
const props = defineProps<{ type: WorkItemType; class?: string }>()

const glyphs = {
  epic: { icon: Crown, tone: 'text-agent' },
  feature: { icon: Trophy, tone: 'text-info' },
  story: { icon: Bookmark, tone: 'text-success' },
  bug: { icon: Bug, tone: 'text-destructive' },
  task: { icon: ListChecks, tone: 'text-warning' },
} as const

const glyph = computed(() => glyphs[props.type])
</script>

<template>
  <component
    :is="glyph.icon"
    :class="cn('size-4 flex-none', glyph.tone, props.class)"
    :aria-label="typeLabels[props.type]"
    role="img"
  />
</template>
