<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { computed } from 'vue'

import { cn } from '@/lib/utils'

/**
 * A person or an agent. The bot badge is not decoration: an agent's comments, transitions
 * and assignments must never be mistaken for a colleague's, so every avatar carries the
 * distinction rather than relying on the name.
 */
const props = withDefaults(
  defineProps<{
    name: string
    isAgent?: boolean
    src?: string | null
    size?: 'sm' | 'md' | 'lg'
    class?: string
  }>(),
  { isAgent: false, src: null, size: 'md', class: undefined },
)

const initials = computed(() =>
  props.name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join(''),
)

const sizes = { sm: 'size-5 text-[9px]', md: 'size-6 text-[10px]', lg: 'size-9 text-xs' } as const
</script>

<template>
  <span :class="cn('relative inline-flex flex-none', props.class)">
    <img v-if="src" :src="src" :alt="name" :class="cn('rounded-full object-cover', sizes[size])" />
    <span
      v-else
      :class="
        cn(
          'bg-secondary text-secondary-foreground grid place-items-center rounded-full font-medium',
          sizes[size],
        )
      "
      :title="name"
      :aria-label="name"
    >
      {{ initials }}
    </span>
    <span
      v-if="isAgent"
      class="bg-agent text-background absolute -right-0.5 -bottom-0.5 grid size-3 place-items-center rounded-full"
      :title="`${name} is an agent`"
      :aria-label="`${name} is an agent`"
    >
      <Bot class="size-2" />
    </span>
  </span>
</template>
