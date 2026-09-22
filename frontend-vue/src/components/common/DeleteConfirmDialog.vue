<script setup lang="ts">
import { Loader2, Trash2, TriangleAlert } from '@lucide/vue'
import { computed, ref, useId, watch } from 'vue'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'

/**
 * The one way Aictiq asks before deleting something for good - a wiki page, a work item, a
 * project, an organization. Deletion is permanent everywhere (there is no trash), so the
 * dialog says exactly what goes and asks for the name to be typed: a confirmation a stray
 * click or an Enter key cannot produce. The caller supplies what is lost; the `details` slot
 * takes a list of what else goes with it (subpages, child items).
 */
const props = withDefaults(
  defineProps<{
    /** Shown in the title: “Delete {name} permanently?” and what must be typed. */
    name: string
    /** Bold lead-in to the consequence list, e.g. “Deleting removes this project, along with:”. */
    summary: string
    consequences: string[]
    confirmLabel: string
    pending?: boolean
    /** A server-side refusal to show under the input, such as a 403 or a mistyped name. */
    error?: string | null
  }>(),
  { pending: false, error: null },
)
const open = defineModel<boolean>('open', { required: true })
const emit = defineEmits<{ confirm: [] }>()

const inputId = useId()
const typed = ref('')
watch(open, (value) => {
  if (value) typed.value = ''
})

const confirmed = computed(
  () =>
    typed.value.trim().toLowerCase() === props.name.trim().toLowerCase() &&
    props.name.trim() !== '',
)

function confirm() {
  if (confirmed.value && !props.pending) emit('confirm')
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="sm:max-w-xl">
      <DialogHeader>
        <DialogTitle class="flex items-center gap-2 text-lg">
          <TriangleAlert class="text-destructive size-5 flex-none" aria-hidden="true" />
          <span class="min-w-0 break-words">Delete “{{ name }}” permanently?</span>
        </DialogTitle>
        <DialogDescription
          >This cannot be undone. There is no trash and no restore.</DialogDescription
        >
      </DialogHeader>

      <div class="border-destructive/40 bg-destructive/10 space-y-2 rounded-lg border p-4 text-sm">
        <p class="font-medium">{{ summary }}</p>
        <ul class="text-foreground/80 list-disc space-y-0.5 pl-5">
          <li v-for="line in consequences" :key="line">{{ line }}</li>
        </ul>
      </div>

      <slot name="details" />

      <div class="space-y-1.5">
        <label :for="inputId" class="text-muted-foreground text-sm">
          Type <span class="text-foreground font-medium">{{ name }}</span> to confirm.
        </label>
        <Input
          :id="inputId"
          v-model="typed"
          :placeholder="name"
          autocomplete="off"
          :aria-invalid="Boolean(error)"
          @keydown.enter="confirm"
        />
        <p v-if="error" class="text-destructive text-sm">{{ error }}</p>
      </div>

      <DialogFooter>
        <Button variant="ghost" :disabled="pending" @click="open = false">Cancel</Button>
        <Button
          class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
          :disabled="!confirmed || pending"
          @click="confirm"
        >
          <Loader2 v-if="pending" class="size-3.5 animate-spin" aria-hidden="true" />
          <Trash2 v-else class="size-3.5" aria-hidden="true" />
          {{ confirmLabel }}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
