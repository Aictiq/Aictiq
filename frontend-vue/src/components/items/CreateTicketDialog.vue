<script setup lang="ts">
import { ref, watch } from 'vue'
import { useQueryClient } from '@tanstack/vue-query'
import { Loader2 } from '@lucide/vue'
import type { WorkItem, WorkItemType } from '@/api/items'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { createTicket } from '@/composables/useCreateTicket'
import { useToast } from '@/composables/useToast'

/**
 * "Create ticket": a type and a quick title. Enter files it, prefilled from the type's default
 * template, and the item detail opens on it for the description, screenshots and the rest.
 */
const props = withDefaults(
  defineProps<{
    slug: string
    projectKey: string
    open: boolean
    teamId?: string | null
    defaultType?: WorkItemType
  }>(),
  { teamId: null, defaultType: 'story' },
)
const emit = defineEmits<{ 'update:open': [open: boolean]; created: [item: WorkItem] }>()

const client = useQueryClient()
const toast = useToast()

// The standalone kinds: Features and Tasks are made beneath their parent.
const types: { value: WorkItemType; label: string }[] = [
  { value: 'story', label: 'Story' },
  { value: 'bug', label: 'Bug' },
  { value: 'epic', label: 'Epic' },
]
const type = ref<WorkItemType>(props.defaultType)
const title = ref('')
const submitting = ref(false)
const titleInput = ref<HTMLInputElement | null>(null)

// The title is what one types first; the type keeps its default unless changed.
function focusTitle(event: Event) {
  event.preventDefault()
  titleInput.value?.focus()
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    type.value = props.defaultType
    title.value = ''
  },
  { immediate: true },
)

async function submit() {
  if (submitting.value || !title.value.trim()) return
  submitting.value = true
  try {
    const created = await createTicket(client, props.slug, props.projectKey, {
      type: type.value,
      title: title.value.trim(),
      teamId: props.teamId,
    })
    toast.success(`${created.key} created.`)
    void client.invalidateQueries({ queryKey: [props.slug, props.projectKey] })
    emit('update:open', false)
    emit('created', created)
  } catch (caught) {
    toast.error(caught, 'The ticket could not be created.')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <Dialog :open="open" @update:open="(value: boolean) => emit('update:open', value)">
    <DialogContent
      class="sm:max-w-lg"
      data-testid="create-ticket-dialog"
      @open-auto-focus="focusTitle"
    >
      <DialogHeader>
        <DialogTitle>Create ticket</DialogTitle>
        <DialogDescription>Give it a title; Enter opens it to fill in the rest.</DialogDescription>
      </DialogHeader>

      <form
        id="create-ticket"
        class="flex items-center gap-2"
        novalidate
        @submit.prevent="submit()"
      >
        <select
          id="create-ticket-type"
          v-model="type"
          class="border-input bg-background h-9 rounded border px-2 text-sm"
          aria-label="Item type"
        >
          <option v-for="option in types" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
        <input
          id="create-ticket-title"
          ref="titleInput"
          v-model="title"
          maxlength="500"
          class="border-input bg-background h-9 min-w-0 flex-1 rounded border px-2 text-sm"
          placeholder="What is it?"
          aria-label="Title"
          required
        />
        <Button
          type="submit"
          data-testid="create-ticket-submit"
          :disabled="submitting || !title.trim()"
        >
          <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
          Create
        </Button>
      </form>
    </DialogContent>
  </Dialog>
</template>
