<script setup lang="ts">
import { UserRound } from '@lucide/vue'
import { computed } from 'vue'

import { avatarUrl } from '@/api/profile'
import UserAvatar from '@/components/common/UserAvatar.vue'
import { cn } from '@/lib/utils'

export interface AssigneeOption {
  userId: string
  displayName: string
  avatarKey?: string | null
  isAgent?: boolean
}

/**
 * The assignee picker that sits on a card or a list row: the avatar, then a plain select of
 * Unassigned and the project's members. `collapse` hides the select's text on narrow
 * screens and lays the select invisibly over the avatar, so the row keeps its title.
 *
 * It swallows its own clicks and keys: a row underneath opens its item on click and reads
 * j/k/Enter/Space as list shortcuts, none of which may fire while someone picks a person.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string | null
    members: AssigneeOption[]
    label: string
    disabled?: boolean
    collapse?: boolean
    class?: string
  }>(),
  { disabled: false, collapse: false, class: undefined },
)
const emit = defineEmits<{ change: [assigneeId: string | null] }>()

const assignee = computed(() => props.members.find((member) => member.userId === props.modelValue))
const name = computed(() => assignee.value?.displayName ?? props.modelValue ?? 'Unassigned')

function onChange(event: Event) {
  const value = (event.target as HTMLSelectElement).value || null
  if (value !== props.modelValue) emit('change', value)
}
</script>

<template>
  <span
    :class="cn('relative inline-flex min-w-0 items-center gap-1', props.class)"
    @click.stop
    @mousedown.stop
    @keydown.stop
  >
    <UserAvatar
      v-if="modelValue"
      :name="name"
      :is-agent="assignee?.isAgent"
      :src="avatarUrl(modelValue, assignee?.avatarKey)"
      size="sm"
    />
    <UserRound
      v-else-if="collapse"
      class="text-muted-foreground size-4 flex-none sm:hidden"
      aria-hidden="true"
    />
    <select
      :value="modelValue ?? ''"
      :aria-label="label"
      :title="name"
      :class="
        cn(
          'border-input bg-background max-w-32 min-w-0 rounded border px-1 py-0.5 text-xs disabled:opacity-60',
          collapse && 'absolute inset-0 opacity-0 sm:static sm:opacity-100',
        )
      "
      :disabled="disabled"
      @change="onChange"
    >
      <option value="">Unassigned</option>
      <option v-for="member in members" :key="member.userId" :value="member.userId">
        {{ member.displayName }}
      </option>
    </select>
  </span>
</template>
