<script setup lang="ts">
import { computed } from 'vue'

import type { Project } from '@/api/projects'

/**
 * A project's icon-or-initial chip. One component because it appears in the sidebar, the
 * project list and every picker, and three hand-rolled versions would drift apart.
 *
 * The colour is a per-project choice and therefore the one place a literal colour is
 * legitimate - it comes from the row, not from the palette. Everything around it stays on
 * the design tokens, so the chip reads correctly in both themes.
 */
const props = withDefaults(
  defineProps<{ project: Pick<Project, 'key' | 'name' | 'icon' | 'color'>; size?: 'sm' | 'md' }>(),
  { size: 'sm' },
)

const initial = computed(() => props.project.key.slice(0, 2))
</script>

<template>
  <span
    class="text-foreground/80 border-border/60 inline-flex flex-none items-center justify-center rounded border font-medium"
    :class="size === 'sm' ? 'size-4.5 text-[9px]' : 'size-7 text-[11px]'"
    :style="project.color ? { backgroundColor: `${project.color}26`, borderColor: project.color } : undefined"
    aria-hidden="true"
  >
    <template v-if="project.icon">{{ project.icon }}</template>
    <template v-else>{{ initial }}</template>
  </span>
</template>
