<script setup lang="ts">
import { computed } from 'vue'

import { cn } from '@/lib/utils'

/**
 * A role dropdown that greys out the roles the person using it may not assign.
 *
 * The gating is a *courtesy*, never the guard - `MembershipRules` on the server decides,
 * and every one of these refusals is also a 403. What it buys is that an admin never sees
 * "Owner" offered and then rejected, which reads as a bug rather than as a rule.
 *
 * A native `<select>` on purpose: role assignment is a one-of-four choice that wants
 * keyboard, screen-reader and mobile behaviour the platform already has, and a disabled
 * `<option>` is the one widely-supported way to show a choice that exists but is not
 * yours to make.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string
    roles: readonly string[]
    /** Answers "may the person using this assign that role?" - usually `canAssignRole`. */
    canAssign?: (role: string) => boolean
    disabled?: boolean
    label: string
    class?: string
  }>(),
  { canAssign: () => true, disabled: false, class: undefined },
)

const emit = defineEmits<{ 'update:modelValue': [string] }>()

/**
 * The current role is always listed, even when the viewer could not assign it - a select
 * whose own value is missing renders blank and reads as "no role", which is worse than
 * showing a choice that cannot be picked.
 */
const options = computed(() =>
  props.roles.map((role) => ({
    role,
    disabled: role !== props.modelValue && !props.canAssign(role),
  })),
)

/**
 * Nothing to choose between: every role other than the one already set is out of reach,
 * which is what an admin looking at a peer sees. Disabling the control says that once,
 * rather than leaving a dropdown that opens onto a list of refusals.
 */
const locked = computed(() =>
  options.value.every((option) => option.disabled || option.role === props.modelValue),
)
</script>

<template>
  <select
    :value="modelValue"
    :disabled="disabled || locked"
    :aria-label="label"
    :class="
      cn(
        'border-border bg-background focus-visible:ring-ring h-7 rounded-lg border px-2 text-xs capitalize focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50',
        props.class,
      )
    "
    @change="emit('update:modelValue', ($event.target as HTMLSelectElement).value)"
  >
    <option v-for="option in options" :key="option.role" :value="option.role" :disabled="option.disabled">
      {{ option.role }}
    </option>
  </select>
</template>
