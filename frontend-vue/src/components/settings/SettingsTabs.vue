<script setup lang="ts">
import { RouterLink } from 'vue-router'

import type { SettingsLink } from '@/router/paths'

/**
 * The tab strip across a settings hub. It scrolls rather than wraps: seven tabs fit on a
 * laptop and do not on a phone, and a second row would push the form below the fold on
 * exactly the screens that can least afford it.

 */
defineProps<{ links: SettingsLink[] }>()
</script>

<template>
  <nav class="border-border -mx-1 flex items-center gap-4 overflow-x-auto border-b px-1">
    <RouterLink
      v-for="link in links"
      :key="link.to"
      :to="link.to"
      :data-tour="link.tour"
      class="text-muted-foreground hover:text-foreground -mb-px flex flex-none items-center gap-1.5 border-b-2 border-transparent px-1 py-2 text-[12.5px]"
      active-class="border-primary! text-foreground! font-medium"
    >
      {{ link.label }}
    </RouterLink>
  </nav>
</template>
