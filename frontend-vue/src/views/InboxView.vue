<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import type { Notification } from '@/api/notifications'
import AppShell from '@/components/shell/AppShell.vue'
import NotificationList from '@/components/notifications/NotificationList.vue'
import { useNotifications } from '@/composables/useNotifications'

const { notifications, markRead } = useNotifications()
const rows = computed(() => notifications.data.value ?? [])
const hasUnread = computed(() => rows.value.some((entry) => !entry.readAt))
function read(entry: Notification) {
  if (!entry.readAt) markRead.mutate({ ids: [entry.id] })
}
function archiveFirstUnread(event: KeyboardEvent) {
  if (
    event.key !== 'e' ||
    event.ctrlKey ||
    event.metaKey ||
    event.altKey ||
    (event.target instanceof HTMLElement &&
      event.target.closest('input, textarea, select, [contenteditable="true"]')) ||
    markRead.isPending.value
  )
    return
  const first = rows.value.find((entry) => !entry.readAt)
  if (first) {
    event.preventDefault()
    read(first)
  }
}
onMounted(() => window.addEventListener('keydown', archiveFirstUnread))
onBeforeUnmount(() => window.removeEventListener('keydown', archiveFirstUnread))
</script>

<template>
  <AppShell>
    <section class="w-full px-5 py-8">
      <div class="flex items-center justify-between gap-4">
        <div>
          <h1 class="text-xl font-semibold">Inbox</h1>
          <p class="text-muted-foreground text-sm">
            Updates from work you follow. Press <kbd>e</kbd> to mark the next unread update read.
          </p>
        </div>
        <button
          type="button"
          class="rounded border px-3 py-1.5 text-sm disabled:opacity-50"
          :disabled="markRead.isPending.value || !hasUnread"
          @click="markRead.mutate({ all: true })"
        >
          Mark all read
        </button>
      </div>
      <p v-if="notifications.isPending.value" class="text-muted-foreground mt-8">
        Loading notifications…
      </p>
      <p v-else-if="notifications.isError.value" class="text-destructive mt-8">
        Notifications could not be loaded.
      </p>
      <p v-else-if="!rows.length" class="text-muted-foreground mt-8">You’re all caught up.</p>
      <NotificationList v-else class="mt-5 rounded border" :entries="rows" @select="read" />
    </section>
  </AppShell>
</template>
