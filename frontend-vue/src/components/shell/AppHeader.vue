<script setup lang="ts">
import { Menu, Monitor, Moon, Search, Sun } from '@lucide/vue'
import { useRoute } from 'vue-router'
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/vue-query'

import NotificationsPopover from '@/components/notifications/NotificationsPopover.vue'
import KeyChip from '@/components/common/KeyChip.vue'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { useCommandStore } from '@/composables/useCommands'
import { useSessionStore } from '@/stores/session'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useUiStore, type ThemePreference } from '@/stores/ui'
import { createHubConnection } from '@/utils/realtime'

/**
 * Breadcrumbs on the left, the search trigger on the right. The trigger opens the command
 * palette rather than a separate search box - one surface for "find or do anything" is
 * the whole point of a palette, and two would compete.
 */
const commands = useCommandStore()
const route = useRoute()
const session = useSessionStore()
const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const ui = useUiStore()
const client = useQueryClient()
let notificationConnection: HubConnection | null = null

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

// The trigger shows the theme in effect, so `system` reads as whatever the OS resolved to.
const themeOptions = [
  { value: 'light', label: 'Light', icon: Sun },
  { value: 'dark', label: 'Dark', icon: Moon },
  { value: 'system', label: 'System', icon: Monitor },
] as const
const themeModel = computed({
  get: () => ui.theme,
  set: (next: ThemePreference) => ui.setTheme(next),
})
const scopeTitle = computed(() => {
  // Scoped pages follow the URL, even while the sidebar's selection is changing.
  // The unscoped board reads its project from the store, just like its content does.
  const projectKey =
    typeof route.params.projectKey === 'string'
      ? route.params.projectKey
      : route.name === 'board'
        ? projects.currentKey
        : null
  if (!projectKey) return 'Aictiq'

  // Project keys are only unique within an organization. Never use another tenant's name.
  if (route.params.slug && route.params.slug !== organizations.currentSlug) return projectKey
  return projects.projects.find((project) => project.key === projectKey)?.name ?? projectKey
})
const crumbs = computed(() => [scopeTitle.value, currentTitle.value].filter(Boolean))
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

    <DropdownMenu>
      <DropdownMenuTrigger
        data-tour="theme-switcher"
        class="text-muted-foreground hover:bg-accent hover:text-foreground focus-visible:ring-ring grid size-9 flex-none place-items-center rounded-md focus-visible:ring-2 focus-visible:outline-none"
        :aria-label="`Theme: ${ui.theme}`"
      >
        <Moon v-if="ui.resolvedTheme === 'dark'" class="size-4" aria-hidden="true" />
        <Sun v-else class="size-4" aria-hidden="true" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" class="w-40">
        <DropdownMenuLabel class="font-label">Theme</DropdownMenuLabel>
        <DropdownMenuRadioGroup v-model="themeModel">
          <DropdownMenuRadioItem
            v-for="option in themeOptions"
            :key="option.value"
            :value="option.value"
          >
            <component :is="option.icon" class="size-3.5" aria-hidden="true" />
            {{ option.label }}
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>

    <NotificationsPopover />

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
