<script setup lang="ts">
import { Bell, CalendarRange, FileText, UserPlus } from '@lucide/vue'
import { computed } from 'vue'
import type { Notification } from '@/api/notifications'
import { avatarUrl } from '@/api/profile'
import ItemTypeIcon from '@/components/common/ItemTypeIcon.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import { notificationAction, notificationKindLabel, relativeTime } from '@/utils/notifications'

/**
 * One notification: who did what, then on which item, then where that item stands now.
 * A row that names nobody - written before actors were recorded, or by an agent the reader
 * may not see - says what it always said.
 */
const props = defineProps<{ entry: Notification }>()

const action = computed(() => notificationAction(props.entry))
const unread = computed(() => !props.entry.readAt)
const subjectIcon = computed(() => {
  if (props.entry.kind === 'sprintStarted' || props.entry.kind === 'sprintCompleted')
    return CalendarRange
  if (props.entry.kind === 'wikiMentioned') return FileText
  if (props.entry.kind === 'inviteAccepted') return UserPlus
  return null
})
const meta = computed(() =>
  [
    props.entry.itemKey ??
      (props.entry.kind === 'reacted'
        ? 'Reacted to your comment'
        : notificationKindLabel(props.entry.kind)),
    props.entry.itemState,
  ]
    .filter(Boolean)
    .join(' • '),
)
const exact = computed(() => new Date(props.entry.createdAt).toLocaleString())
</script>

<template>
  <span class="flex min-w-0 flex-1 gap-3">
    <UserAvatar
      v-if="entry.actor"
      :name="entry.actor.displayName"
      :is-agent="entry.actor.isAgent"
      :src="avatarUrl(entry.actor.id, entry.actor.avatarKey)"
      size="lg"
      class="mt-0.5"
    />
    <span
      v-else
      class="bg-secondary text-secondary-foreground mt-0.5 grid size-9 flex-none place-items-center rounded-full"
      aria-hidden="true"
    >
      <Bell class="size-4" />
    </span>
    <span class="min-w-0 flex-1">
      <span
        data-testid="notification-headline"
        class="block text-sm"
        :class="unread && 'font-semibold'"
      >
        <template v-if="entry.actor && action">
          <span>{{ entry.actor.displayName }}</span
          >{{ action.startsWith('’') ? '' : ' '
          }}<span :class="!unread && 'text-muted-foreground'">{{ action }}</span>
        </template>
        <template v-else>{{ entry.message }}</template>
        <span class="text-muted-foreground text-xs font-normal">
          ·
          <time :datetime="entry.createdAt" :title="exact" class="whitespace-nowrap">{{
            relativeTime(entry.createdAt)
          }}</time></span
        >
      </span>
      <span
        v-if="entry.itemTitle"
        data-testid="notification-item"
        class="mt-1 flex min-w-0 items-center gap-1.5 text-sm"
      >
        <ItemTypeIcon v-if="entry.itemType" :type="entry.itemType" />
        <component
          :is="subjectIcon"
          v-else-if="subjectIcon"
          class="text-muted-foreground size-4 flex-none"
        />
        <span class="truncate">{{ entry.itemTitle }}</span>
      </span>
      <span data-testid="notification-meta" class="text-muted-foreground mt-0.5 block text-xs">{{
        meta
      }}</span>
    </span>
  </span>
</template>
