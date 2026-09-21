<script setup lang="ts">
import { Loader2 } from '@lucide/vue'

/**
 * The three states every data-backed view needs before it has anything to show.
 * Keeping them in one component stops each page inventing its own spinner and its own
 * idea of what "empty" looks like.
 */
withDefaults(
  defineProps<{
    state: 'loading' | 'empty' | 'error'
    title?: string
    description?: string
  }>(),
  { title: undefined, description: undefined },
)
</script>

<template>
  <div class="flex flex-col items-center justify-center gap-2 px-6 py-16 text-center">
    <Loader2 v-if="state === 'loading'" class="text-muted-foreground size-5 animate-spin" />
    <p v-if="title" class="text-sm font-medium">{{ title }}</p>
    <p v-if="description" class="text-muted-foreground max-w-sm text-sm">
      {{ description }}
    </p>
    <slot />
  </div>
</template>
