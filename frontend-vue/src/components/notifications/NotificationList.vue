<script setup lang="ts">
import { RouterLink } from 'vue-router'
import type { Notification } from '@/api/notifications'
import { factoryRunPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'

defineProps<{ entries: Notification[] }>()
const emit = defineEmits<{ select: [entry: Notification] }>()
const organizations = useOrganizationsStore()

function destination(entry: Notification) {
  const organization = organizations.organizations.find((org) => org.id === entry.organizationId)
  const slug = organization?.slug
  if (slug && entry.runId && organization?.canOperateFactory) {
    return factoryRunPath(slug, entry.runId)
  }
  const key = entry.itemKey
  const separator = key?.lastIndexOf('-') ?? -1
  if (!slug || !key || separator < 1) return '/inbox'
  return {
    name: 'item-detail',
    params: { slug, projectKey: key.slice(0, separator), itemKey: key },
  }
}
</script>

<template>
  <ul class="divide-border divide-y">
    <li v-for="entry in entries" :key="entry.id">
      <RouterLink
        :to="destination(entry)"
        class="hover:bg-accent focus-visible:bg-accent flex gap-3 px-4 py-3 outline-none"
        :class="!entry.readAt && 'bg-muted/40'"
        @click="emit('select', entry)"
      >
        <span class="min-w-0 flex-1">
          <span class="block text-sm" :class="!entry.readAt && 'font-semibold'">{{
            entry.message
          }}</span>
          <span class="text-muted-foreground mt-1 block text-xs">
            {{ entry.itemKey ?? entry.kind }} · {{ new Date(entry.createdAt).toLocaleString() }}
          </span>
        </span>
        <span
          v-if="!entry.readAt"
          class="bg-primary mt-1.5 size-2 shrink-0 rounded-full"
          aria-label="Unread"
        />
      </RouterLink>
    </li>
  </ul>
</template>
