<script setup lang="ts">
import { computed, ref } from 'vue'

import { logTime, type TimeTrackingItem } from '@/api/time-tracking'
import { useToast } from '@/composables/useToast'

const props = withDefaults(
  defineProps<{
    slug: string
    itemKey: string
    version: number
    remainingHours: number | null
    completedHours: number | null
    disabled?: boolean
  }>(),
  { disabled: false },
)

const emit = defineEmits<{ logged: [item: TimeTrackingItem] }>()
const { error, success } = useToast()

const open = ref(false)
const draftHours = ref('')
const saving = ref(false)
const validationMessage = ref<string | null>(null)

const remainingLabel = computed(() => formatHours(props.remainingHours))
const completedLabel = computed(() => formatHours(props.completedHours))

function formatHours(hours: number | null) {
  return hours === null ? '-' : `${hours}h`
}

function close() {
  open.value = false
  validationMessage.value = null
}

async function submit() {
  const hours = Number(draftHours.value)
  if (!Number.isFinite(hours) || hours <= 0 || hours > 9999.99 || !/^\d+(?:\.\d{1,2})?$/.test(draftHours.value)) {
    validationMessage.value = 'Enter a positive number of hours with no more than two decimal places.'
    return
  }

  saving.value = true
  validationMessage.value = null
  try {
    const item = await logTime(props.slug, props.itemKey, hours, props.version)
    emit('logged', item)
    draftHours.value = ''
    open.value = false
    success('Time logged', `${formatHours(hours)} added to ${props.itemKey}.`)
  } catch (exception) {
    error(exception, 'Could not log time.')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="relative inline-flex">
    <button
      type="button"
      class="border-input bg-background hover:bg-accent inline-flex items-center rounded-md border px-2.5 py-1.5 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-50"
      :disabled="disabled"
      :aria-expanded="open"
      aria-haspopup="dialog"
      @click="open = !open"
    >
      Log time
    </button>

    <form
      v-if="open"
      class="bg-popover text-popover-foreground ring-foreground/10 absolute right-0 top-full z-20 mt-2 w-64 rounded-lg p-3 shadow-lg ring-1"
      aria-label="Log time"
      @submit.prevent="submit"
    >
      <div class="mb-3 grid grid-cols-2 gap-2 text-xs">
        <span class="text-muted-foreground">Remaining <strong class="text-foreground">{{ remainingLabel }}</strong></span>
        <span class="text-muted-foreground">Completed <strong class="text-foreground">{{ completedLabel }}</strong></span>
      </div>

      <label class="font-label mb-1 block text-sm" :for="`log-time-${itemKey}`">Hours</label>
      <input
        :id="`log-time-${itemKey}`"
        v-model="draftHours"
        type="number"
        min="0.01"
        max="9999.99"
        step="0.01"
        inputmode="decimal"
        required
        autofocus
        class="border-input bg-background focus-visible:ring-ring w-full rounded-md border px-2 py-1.5 text-sm focus-visible:ring-2 focus-visible:outline-none"
        :aria-describedby="validationMessage ? `log-time-error-${itemKey}` : undefined"
      >
      <p v-if="validationMessage" :id="`log-time-error-${itemKey}`" class="text-destructive mt-1 text-xs" role="alert">
        {{ validationMessage }}
      </p>

      <div class="mt-3 flex justify-end gap-2">
        <button type="button" class="hover:bg-accent rounded-md px-2.5 py-1.5 text-sm" @click="close">Cancel</button>
        <button
          type="submit"
          class="bg-primary text-primary-foreground hover:bg-primary/90 rounded-md px-2.5 py-1.5 text-sm font-medium disabled:cursor-wait disabled:opacity-50"
          :disabled="saving"
        >
          {{ saving ? 'Logging…' : 'Log time' }}
        </button>
      </div>
    </form>
  </div>
</template>
