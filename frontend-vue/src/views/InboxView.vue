<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { markNotificationsRead, listNotifications } from '@/api/notifications'
import AppShell from '@/components/shell/AppShell.vue'
const client = useQueryClient(); const notifications = useQuery({ queryKey: ['notifications'], queryFn: () => listNotifications() })
const rows = computed(() => notifications.data.value ?? [])
async function read(ids?: string[], all = false) { await markNotificationsRead(ids, all); await client.invalidateQueries({ queryKey: ['notifications'] }) }
function archiveFirstUnread(event: KeyboardEvent) { if (event.key !== 'e' || event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return; const first = rows.value.find(x => !x.readAt); if (first) { event.preventDefault(); void read([first.id]) } }
onMounted(() => window.addEventListener('keydown', archiveFirstUnread))
onBeforeUnmount(() => window.removeEventListener('keydown', archiveFirstUnread))
</script>
<template><AppShell><section class="w-full px-5 py-8"><div class="flex items-center justify-between"><div><h1 class="text-xl font-semibold">Inbox</h1><p class="text-muted-foreground text-sm">Updates from work you follow. Press <kbd>e</kbd> to archive the next unread update.</p></div><button class="border rounded px-3 py-1.5 text-sm" @click="read(undefined, true)">Mark all read</button></div><p v-if="notifications.isError.value" class="text-destructive mt-8">Notifications could not be loaded.</p><p v-else-if="!rows.length" class="text-muted-foreground mt-8">You’re all caught up.</p><div v-else class="mt-5 divide-y rounded border"><article v-for="entry in rows" :key="entry.id" class="flex items-start justify-between gap-3 p-4" :class="!entry.readAt && 'bg-muted/40'"><div><p class="text-sm">{{ entry.message }}</p><p class="text-muted-foreground mt-1 text-xs">{{ entry.itemKey ?? entry.kind }} · {{ new Date(entry.createdAt).toLocaleString() }}</p></div><button v-if="!entry.readAt" class="text-xs underline" @click="read([entry.id])">Read</button></article></div></section></AppShell></template>
