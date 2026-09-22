<script setup lang="ts">
/**
 * A titled block inside a settings tab, and the one place the reading width is decided.
 *
 * `narrow` is the default because most of these are forms: a 60-character line is what
 * makes a field label and its explanation readable, and the hub's container is wide for
 * the tables, not for the prose.
 */
withDefaults(defineProps<{ title?: string; description?: string; wide?: boolean }>(), {
  title: undefined,
  description: undefined,
  wide: false,
})
</script>

<template>
  <section :class="wide ? '' : 'max-w-2xl'">
    <header v-if="title" class="mb-3">
      <h2 class="text-sm font-medium">{{ title }}</h2>
      <p v-if="description" class="text-muted-foreground mt-0.5 text-xs">{{ description }}</p>
    </header>
    <slot />
  </section>
</template>
