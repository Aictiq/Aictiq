<script setup lang="ts">
import { Bell, Menu, Search } from '@lucide/vue'
import { RouterLink, useRoute } from 'vue-router'
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/vue-query'

import KeyChip from '@/components/common/KeyChip.vue'
import { useCommandStore } from '@/composables/useCommands'
import { useSessionStore } from '@/stores/session'
import { useUiStore } from '@/stores/ui'
import { createHubConnection } from '@/utils/realtime'

/**
 * Breadcrumbs on the left, the search trigger on the right. The trigger opens the command
 * palette rather than a separate search box - one surface for "find or do anything" is
 * the whole point of a palette, and two would compete.
 */
const commands = useCommandStore()
const route = useRoute()
const session = useSessionStore()
const ui = useUiStore()
const client = useQueryClient()
let notificationConnection: HubConnection | null = null
const unreadCount = computed(() => session.user?.unreadCount ?? 0)

// The badge is fed by the session payload; the hub only tells it when to refetch. A hub
// that will not connect must therefore not break the header it lives in.
const warn = (error: unknown) => {
  if (import.meta.env.DEV) console.warn('[realtime]', error)
}

onMounted(() => {
  if (!session.isAuthenticated) return
  const connection = createHubConnection('/hubs/projects')
  notificationConnection = connection
  connection.on('notification.new', async () => {
    await client.invalidateQueries({ queryKey: ['notifications'] })
    await session.reload()
  })
  connection.start().catch(warn)
})

onBeforeUnmount(() => {
  const active = notificationConnection
  notificationConnection = null
  if (active && active.state !== HubConnectionState.Disconnected) active.stop().catch(warn)
})

// The current route's title is both more useful and more compact than an internal route
// name such as `org-settings-members`, especially in the phone header.
const currentTitle = computed(() => {
  if (typeof route.meta.title === 'string') return route.meta.title
  if (route.name === 'home') return 'My work'
  return String(route.name ?? '')
    .replace(/^(organization|org|project|team)-/, '')
    .replace(/-settings$/, '')
    .replaceAll('-', ' ')
    .replace(/^./, (letter) => letter.toUpperCase())
})
const crumbs = computed(() => ['Aictiq', currentTitle.value].filter(Boolean))
</script>

<template>
  <header
    class="bg-header border-border flex flex-none items-center gap-1.5 border-b px-2 sm:gap-2.5 sm:px-4"
    :style="{ height: 'var(--spacing-header)' }"
  >
    <button
      type="button"
      class="text-muted-foreground hover:bg-accent hover:text-foreground inline-grid size-9 flex-none place-items-center rounded-md md:hidden"
      aria-label="Open navigation"
      aria-controls="mobile-navigation"
      :aria-expanded="ui.mobileSidebarOpen"
      @click="ui.setMobileSidebarOpen(true)"
    >
      <Menu class="size-5" aria-hidden="true" />
    </button>

    <nav
      aria-label="Breadcrumb"
      class="text-muted-foreground min-w-0 flex items-center gap-1.5 overflow-hidden text-[12.5px]"
    >
      <template v-for="(crumb, index) in crumbs" :key="crumb">
        <span v-if="index > 0" class="text-border hidden sm:inline" aria-hidden="true">/</span>
        <span
          class="truncate"
          :class="[
            index === crumbs.length - 1 && 'text-foreground font-medium',
            index < crumbs.length - 1 && 'hidden sm:inline',
          ]"
          >{{ crumb }}</span
        >
      </template>
    </nav>

    <div class="flex-1" />

    <RouterLink
      to="/inbox"
      data-tour="inbox-link"
      class="text-muted-foreground hover:bg-accent hover:text-foreground relative grid size-9 flex-none place-items-center rounded-md"
      aria-label="Inbox"
    >
      <Bell class="size-4" aria-hidden="true" />
      <span
        v-if="unreadCount"
        class="bg-primary text-primary-foreground absolute -right-1 -top-1 min-w-4 rounded-full px-1 text-center text-[10px]"
        >{{ unreadCount > 99 ? '99+' : unreadCount }}</span
      >
    </RouterLink>

    <button
      type="button"
      data-tour="search"
      class="text-muted-foreground hover:text-foreground border-border hover:border-ring/40 grid size-9 flex-none place-items-center rounded-md border sm:flex sm:size-auto sm:gap-2 sm:px-2 sm:py-1"
      aria-label="Search or jump to"
      @click="commands.show()"
    >
      <Search class="size-4 sm:size-3" aria-hidden="true" />
      <span class="hidden text-xs sm:inline">Search or jump to…</span>
      <KeyChip class="hidden lg:inline-flex" binding="mod+k" />
    </button>
  </header>
</template>
