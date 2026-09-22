<script setup lang="ts">
import { useRoute } from 'vue-router'
import { computed } from 'vue'

import AppShell from '@/components/shell/AppShell.vue'
import EmptyState from '@/components/common/EmptyState.vue'

/**
 * Stands in for every navigation target the sidebar offers before its module exists.
 * Having the route resolve - rather than 404 - is what lets the shell's navigation and
 * shortcuts be exercised now.
 */
const route = useRoute()
const title = computed(() => String(route.meta.title ?? route.name ?? 'Coming soon'))
const soon = computed(() => route.meta.soon === true)
</script>

<template>
  <AppShell>
    <EmptyState
      :title="title"
      :description="soon ? 'This screen is not built yet.' : undefined"
      icon="◇"
    />
  </AppShell>
</template>
