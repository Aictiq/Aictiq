<script setup lang="ts">
import type { HTMLAttributes } from 'vue'
import { Eye, EyeOff } from '@lucide/vue'
import { ref } from 'vue'

import { cn } from '@/lib/utils'
import Input from './Input.vue'

/**
 * A password field with an eye that reveals what was typed. Typing a long password blind
 * is where most sign-in failures come from, so the reveal is worth the shoulder-surfing
 * risk - but only while they ask for it: the field starts masked on every mount, and
 * nothing here remembers that it was ever open.
 */

defineOptions({ inheritAttrs: false })

const props = defineProps<{
  class?: HTMLAttributes['class']
}>()

const model = defineModel<string>({ default: '' })

const revealed = ref(false)
</script>

<template>
  <div class="relative">
    <!-- `$attrs` carries id, autocomplete, required and aria-invalid through to the real
         input rather than dropping them on the wrapper. -->
    <Input
      v-bind="$attrs"
      v-model="model"
      :type="revealed ? 'text' : 'password'"
      :class="cn('pr-8', props.class)"
    />
    <button
      type="button"
      class="text-muted-foreground hover:text-foreground focus-visible:ring-ring/50 absolute top-1/2 right-2 -translate-y-1/2 rounded p-0.5 outline-none focus-visible:ring-3"
      :aria-label="revealed ? 'Hide password' : 'Show password'"
      @click="revealed = !revealed"
    >
      <component :is="revealed ? EyeOff : Eye" class="size-3.5" aria-hidden="true" />
    </button>
  </div>
</template>
