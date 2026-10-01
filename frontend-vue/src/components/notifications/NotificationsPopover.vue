<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { Bell } from '@lucide/vue'
import { PopoverRoot, PopoverTrigger, PopoverPortal, PopoverContent } from 'reka-ui'
import type { Notification } from '@/api/notifications'
import NotificationList from '@/components/notifications/NotificationList.vue'
import { useNotifications } from '@/composables/useNotifications'
import { useSessionStore } from '@/stores/session'

const open = ref(false)
const route = useRoute()
const session = useSessionStore()
const { notifications, markRead } = useNotifications()
const rows = computed(() => notifications.data.value ?? [])
const unreadCount = computed(() => session.user?.unreadCount ?? 0)
watch(
  () => route.fullPath,
  () => {
    open.value = false
  },
)

function select(entry: Notification) {
  open.value = false
  if (!entry.readAt) markRead.mutate({ ids: [entry.id] })
}
</script>

<template>
  <PopoverRoot v-model:open="open">
    <PopoverTrigger
      data-tour="inbox-link"
      class="text-muted-foreground hover:bg-accent hover:text-foreground focus-visible:ring-ring relative grid size-9 flex-none place-items-center rounded-md focus-visible:ring-2 focus-visible:outline-none"
      aria-label="Notifications"
    >
      <Bell class="size-4" aria-hidden="true" />
      <span
        v-if="unreadCount > 0"
        data-testid="notification-badge"
        class="bg-primary text-primary-foreground absolute -right-1 -top-1 min-w-4 rounded-full px-1 text-center text-[10px]"
        >{{ unreadCount > 99 ? '99+' : unreadCount }}</span
      >
    </PopoverTrigger>
    <PopoverPortal>
      <PopoverContent
        align="end"
        :side-offset="8"
        aria-label="Notifications"
        class="bg-popover text-popover-foreground border-border z-50 flex max-h-[min(32rem,var(--reka-popover-content-available-height))] w-[min(24rem,calc(100vw-1rem))] flex-col overflow-hidden rounded-lg border shadow-lg"
      >
        <div class="border-border flex items-center justify-between gap-4 border-b px-4 py-3">
          <h2 class="text-sm font-semibold">Notifications</h2>
          <button
            type="button"
            class="text-muted-foreground hover:text-foreground text-xs disabled:opacity-50"
            :disabled="markRead.isPending.value || unreadCount === 0"
            @click="markRead.mutate({ all: true })"
          >
            Mark all read
          </button>
        </div>
        <div class="min-h-0 overflow-y-auto" aria-live="polite">
          <p v-if="notifications.isPending.value" class="text-muted-foreground p-6 text-sm">
            Loading notifications…
          </p>
          <div v-else-if="notifications.isError.value" class="p-6 text-sm">
            <p class="text-destructive">Notifications could not be loaded.</p>
            <button type="button" class="mt-2 underline" @click="notifications.refetch()">
              Try again
            </button>
          </div>
          <p v-else-if="!rows.length" class="text-muted-foreground p-6 text-sm">
            You’re all caught up.
          </p>
          <NotificationList v-else :entries="rows" @select="select" />
        </div>
        <RouterLink
          to="/inbox"
          class="border-border hover:bg-accent border-t px-4 py-3 text-center text-xs"
          @click="open = false"
          >View all notifications</RouterLink
        >
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>
