<script setup lang="ts">
import { ChevronDown } from '@lucide/vue'
import { computed } from 'vue'

import { isSetupStepDone, type SetupStep } from '@/lib/factorySetup'

/**
 * One step of the setup guide: a check that Aictiq ticks when it sees the step done, the line
 * that says what it saw, and the instructions folded underneath. A tick the person gives
 * looks different from one Aictiq gives, because only one of them was verified.
 */
const props = defineProps<{ step: SetupStep; index: number; title: string; open: boolean }>()
defineEmits<{ toggle: []; tick: [value: boolean] }>()

const done = computed(() => isSetupStepDone(props.step.state))

const badge = computed(() => {
  switch (props.step.state) {
    case 'confirmed':
      return { label: 'Confirmed by Aictiq', class: 'text-success' }
    case 'automatic':
      return { label: 'Automatic', class: 'text-success' }
    case 'ticked':
      return { label: 'Ticked by you', class: 'text-muted-foreground' }
    case 'unknown':
      return { label: 'Aictiq cannot see this', class: 'text-warning' }
    default:
      return { label: 'To do', class: 'text-muted-foreground' }
  }
})
</script>

<template>
  <li
    class="setup-step border-border/70 border-b last:border-0"
    :data-state="step.state"
    :data-testid="`setup-step-${step.id}`"
  >
    <div class="flex items-start gap-3 py-2.5">
      <span
        class="relative mt-0.5 flex size-5 flex-none items-center justify-center rounded-full border-2 transition-colors duration-500"
        :class="
          done
            ? step.state === 'ticked'
              ? 'border-muted-foreground/60 bg-muted'
              : 'border-success bg-success'
            : step.state === 'unknown'
              ? 'border-warning/70 border-dashed'
              : 'border-muted-foreground/40'
        "
        role="img"
        :aria-label="done ? `Step ${index + 1} done` : `Step ${index + 1} not done`"
      >
        <svg v-if="done" viewBox="0 0 16 16" class="size-3" aria-hidden="true">
          <path
            d="M3.5 8.5l3 3 6-7"
            fill="none"
            stroke-width="2.2"
            stroke-linecap="round"
            stroke-linejoin="round"
            class="setup-check"
            :class="step.state === 'ticked' ? 'stroke-muted-foreground' : 'stroke-white'"
          />
        </svg>
        <span v-else class="text-muted-foreground text-[10px] font-medium">{{ index + 1 }}</span>
        <span
          v-if="step.state === 'confirmed'"
          class="setup-burst border-success"
          aria-hidden="true"
        />
      </span>

      <button
        type="button"
        class="min-w-0 flex-1 text-left"
        :aria-expanded="open"
        @click="$emit('toggle')"
      >
        <span class="flex items-center gap-2">
          <span class="text-sm font-medium" :class="{ 'text-muted-foreground': done }">{{
            title
          }}</span>
          <span class="text-[11px]" :class="badge.class">{{ badge.label }}</span>
          <ChevronDown
            class="text-muted-foreground ml-auto size-4 flex-none transition-transform duration-300"
            :class="{ 'rotate-180': open }"
            aria-hidden="true"
          />
        </span>
        <span v-if="step.detail" class="text-muted-foreground mt-0.5 block text-xs">{{
          step.detail
        }}</span>
      </button>
    </div>

    <div
      class="setup-body grid transition-[grid-template-rows] duration-300 ease-out"
      :class="open ? 'grid-rows-[1fr]' : 'grid-rows-[0fr]'"
    >
      <div class="min-h-0 overflow-hidden">
        <div class="text-muted-foreground space-y-2 pb-3 pl-8 text-xs">
          <slot />
          <label
            v-if="step.tickable"
            class="text-foreground mt-2 flex w-fit cursor-pointer items-center gap-2"
          >
            <input
              type="checkbox"
              :checked="step.state === 'ticked'"
              :data-testid="`setup-tick-${step.id}`"
              @change="$emit('tick', ($event.target as HTMLInputElement).checked)"
            />
            I did this on the machine
          </label>
        </div>
      </div>
    </div>
  </li>
</template>

<style scoped>
.setup-check {
  stroke-dasharray: 16;
  stroke-dashoffset: 16;
  animation: setup-draw 0.45s 0.1s ease-out forwards;
}

@keyframes setup-draw {
  to {
    stroke-dashoffset: 0;
  }
}

.setup-burst {
  position: absolute;
  inset: -2px;
  border-radius: 9999px;
  border-width: 2px;
  animation: setup-burst 0.8s ease-out forwards;
  pointer-events: none;
}

@keyframes setup-burst {
  from {
    transform: scale(1);
    opacity: 0.9;
  }
  to {
    transform: scale(2.2);
    opacity: 0;
  }
}
</style>
