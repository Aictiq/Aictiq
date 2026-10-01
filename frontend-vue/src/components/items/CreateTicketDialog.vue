<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { Loader2, Sparkles } from '@lucide/vue'
import {
  attachmentAccept,
  attachmentUrl,
  settleAttachments,
  uploadAttachment,
} from '@/api/attachments'
import { createItem, type WorkItem, type WorkItemType } from '@/api/items'
import { getRefinementSettings, refineItem } from '@/api/refinement'
import { listItemTemplates, type ItemTemplate } from '@/api/templates'
import MarkdownEditor from '@/components/common/MarkdownEditor.vue'
import TemplatePicker from '@/components/common/TemplatePicker.vue'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { useToast } from '@/composables/useToast'
import { draftTitle } from '@/lib/refinement'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * "Create ticket": a type, a title and a description, with screenshots and files pasted or
 * dropped into it. **Create** files it as written; **Refine ticket** files it too and hands it
 * to the project's refine playbook, which rewrites it into a complete ticket or asks what it
 * needs - the item opens so the person can follow, answer and confirm.
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
const organizations = useOrganizationsStore()
const canOperate = computed(() => organizations.current?.canOperateFactory === true)

// The standalone kinds: Features and Tasks are made beneath their parent.
const types: { value: WorkItemType; label: string }[] = [
  { value: 'story', label: 'Story' },
  { value: 'bug', label: 'Bug' },
  { value: 'epic', label: 'Epic' },
]
const type = ref<WorkItemType>(props.defaultType)
const title = ref('')
const description = ref('')
const templateId = ref<string | null>(null)
const appliedDescription = ref('')
const selectedTemplate = ref<ItemTemplate | null>(null)
const submitting = ref<'create' | 'refine' | null>(null)
const error = ref<string | null>(null)
const editor = ref<{ uploading: boolean } | null>(null)
// The item has no id until it is created: uploads stay pending until then, like a comment's.
const pending = new Set<string>()

const settings = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'refinement-settings']),
  queryFn: () => getRefinementSettings(props.slug, props.projectKey),
  enabled: computed(() => props.open && canOperate.value),
})
const templates = useQuery({
  queryKey: computed(() => [props.slug, props.projectKey, 'item-templates']),
  queryFn: () => listItemTemplates(props.slug, props.projectKey),
  enabled: computed(() => props.open),
})

function applyTemplate(template: ItemTemplate | null) {
  templateId.value = template?.id ?? null
  selectedTemplate.value = template
  // Only replace empty text or the untouched prefill, never a person's edits.
  if (!description.value.trim() || description.value === appliedDescription.value) {
    description.value = template?.descriptionMarkdown ?? ''
    appliedDescription.value = description.value
  }
}

watch([type, () => templates.isSuccess.value], () => {
  if (!props.open) return
  applyTemplate(
    templates.data.value?.find((template) => template.type === type.value && template.isDefault) ??
      null,
  )
})
const canRefine = computed(() => canOperate.value && settings.data.value?.enabled === true)
const hasDescription = computed(() => description.value.trim().length > 0)
const busy = computed(() => submitting.value !== null || editor.value?.uploading === true)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    type.value = props.defaultType
    title.value = ''
    description.value = ''
    appliedDescription.value = ''
    applyTemplate(
      templates.data.value?.find(
        (template) => template.type === type.value && template.isDefault,
      ) ?? null,
    )
    error.value = null
    pending.clear()
  },
  { immediate: true },
)

async function upload(file: File) {
  const id = await uploadAttachment(props.slug, props.projectKey, file)
  pending.add(id)
  return attachmentUrl(props.slug, id)
}

async function submit(refine: boolean) {
  if (busy.value) return
  if (refine && !canRefine.value) return
  if (refine ? !hasDescription.value : !title.value.trim()) return
  submitting.value = refine ? 'refine' : 'create'
  error.value = null
  let created: WorkItem
  try {
    created = await createItem(props.slug, props.projectKey, {
      type: type.value,
      title: title.value.trim() || draftTitle(description.value),
      descriptionMarkdown: description.value,
      teamId: props.teamId,
      priority: selectedTemplate.value?.defaultPriority ?? undefined,
      labelIds: selectedTemplate.value?.defaultLabelIds,
    })
  } catch (caught) {
    submitting.value = null
    error.value = 'The ticket could not be created.'
    toast.error(caught, 'The ticket could not be created.')
    return
  }
  await settleAttachments(props.slug, [...pending], description.value, { itemId: created.id })
  pending.clear()
  if (refine) {
    try {
      await refineItem(props.slug, created.key)
      toast.success(
        `${created.key} created.`,
        'An agent is refining it - follow along on the ticket.',
      )
    } catch (caught) {
      // The ticket exists either way; it opens so it can be refined again or finished by hand.
      toast.error(caught, `${created.key} was created, but refining it could not start.`)
    }
  } else {
    toast.success(`${created.key} created.`)
  }
  submitting.value = null
  void client.invalidateQueries({ queryKey: [props.slug, props.projectKey] })
  emit('update:open', false)
  emit('created', created)
}
</script>

<template>
  <Dialog :open="open" @update:open="(value: boolean) => emit('update:open', value)">
    <DialogContent class="sm:max-w-2xl" data-testid="create-ticket-dialog">
      <DialogHeader>
        <DialogTitle>Create ticket</DialogTitle>
        <DialogDescription>
          Describe it in your own words; paste or drop screenshots and files into the
          description.<template v-if="canRefine">
            <strong>Refine ticket</strong> lets an agent turn it into a complete ticket, asking only
            what it cannot work out.</template
          >
        </DialogDescription>
      </DialogHeader>

      <form id="create-ticket" class="space-y-4" novalidate @submit.prevent="submit(false)">
        <div class="grid gap-3 sm:grid-cols-[9rem_minmax(0,1fr)]">
          <div class="space-y-1.5">
            <label for="create-ticket-type" class="text-sm font-medium">Type</label>
            <select
              id="create-ticket-type"
              v-model="type"
              class="border-input bg-background w-full rounded border px-2 py-1.5 text-sm"
            >
              <option v-for="option in types" :key="option.value" :value="option.value">
                {{ option.label }}
              </option>
            </select>
          </div>
          <div class="space-y-1.5">
            <label for="create-ticket-title" class="text-sm font-medium">Title</label>
            <input
              id="create-ticket-title"
              v-model="title"
              maxlength="500"
              class="border-input bg-background w-full rounded border px-2 py-1.5 text-sm"
              :placeholder="canRefine ? 'Optional when refining' : 'What is it?'"
            />
          </div>
        </div>
        <TemplatePicker
          v-if="templates.data.value?.some((template) => template.type === type)"
          v-model="templateId"
          :templates="templates.data.value ?? []"
          :type="type"
          :disabled="busy"
          @select="applyTemplate"
        />
        <div class="space-y-1.5">
          <label class="text-sm font-medium">Description</label>
          <MarkdownEditor
            ref="editor"
            v-model="description"
            :upload="upload"
            :accept="attachmentAccept"
            placeholder="What should happen, what goes wrong, who needs it…"
          />
        </div>
        <p v-if="error" class="text-destructive text-sm">{{ error }}</p>
      </form>

      <DialogFooter class="gap-2">
        <Button
          type="button"
          variant="ghost"
          :disabled="submitting !== null"
          @click="emit('update:open', false)"
        >
          Cancel
        </Button>
        <Button
          type="submit"
          form="create-ticket"
          variant="outline"
          data-testid="create-ticket-submit"
          :disabled="busy || !title.trim()"
        >
          <Loader2 v-if="submitting === 'create'" class="animate-spin" aria-hidden="true" />
          Create
        </Button>
        <Button
          v-if="canRefine"
          type="button"
          data-testid="create-ticket-refine"
          :disabled="busy || !hasDescription"
          :title="hasDescription ? undefined : 'Describe the ticket first'"
          @click="submit(true)"
        >
          <Loader2 v-if="submitting === 'refine'" class="animate-spin" aria-hidden="true" />
          <Sparkles v-else aria-hidden="true" />
          Refine ticket
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
