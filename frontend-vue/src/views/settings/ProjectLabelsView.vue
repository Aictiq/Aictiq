<script setup lang="ts">
import { Loader2, Pencil, Plus, Trash2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import { createLabel, deleteLabel, listLabels, updateLabel, type Label } from '@/api/labels'
import { hasProjectRole } from '@/api/projects'
import EmptyState from '@/components/common/EmptyState.vue'
import { presetLabelColors } from '@/components/common/LabelPicker.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
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
import { Textarea } from '@/components/ui/textarea'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { ApiError, ConflictError } from '@/utils/api'

/**
 * A project's own tag vocabulary: what an item can be labelled with, grouped the way the
 * team namespaced it ("type: frontend", "team: platform"), ungrouped last.
 *
 * Every write echoes the `version` it read, exactly like every other settings tab — a
 * label is small enough that two people editing the same one at once is rare, but not
 * rare enough to skip the guarantee.
 */
const project = useProjectScope()
const toast = useToast()

const record = computed(() => project.record.value!)
const mayWrite = computed(() => hasProjectRole(record.value.role, 'member') && !record.value.isArchived)
const mayDelete = computed(() => hasProjectRole(record.value.role, 'admin') && !record.value.isArchived)

const labels = ref<Label[]>([])
const loading = ref(true)
const failed = ref(false)

/** The API already orders group-then-name with nulls last; grouping here only slices that order. */
const sections = computed(() => {
  const result: { group: string | null; labels: Label[] }[] = []
  for (const label of labels.value) {
    const last = result[result.length - 1]
    if (last && last.group === label.group) {
      last.labels.push(label)
    } else {
      result.push({ group: label.group, labels: [label] })
    }
  }
  return result
})

async function load() {
  loading.value = true
  failed.value = false
  try {
    labels.value = await listLabels(project.slug.value, project.projectKey.value)
  } catch (error) {
    labels.value = []
    failed.value = true
    toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([project.slug, project.projectKey], load, { immediate: true })

// ── Create ──────────────────────────────────────────────────────────────────────────
const creating = ref(false)
const submitting = ref(false)
const createFieldErrors = ref<Record<string, string[]>>({})
const createName = ref('')
const createColor = ref(presetLabelColors[0]!.hex)
const createGroup = ref('')
const createDescription = ref('')

function openCreate() {
  createName.value = ''
  createColor.value = presetLabelColors[0]!.hex
  createGroup.value = ''
  createDescription.value = ''
  createFieldErrors.value = {}
  creating.value = true
}

async function submitCreate() {
  submitting.value = true
  createFieldErrors.value = {}
  try {
    const created = await createLabel(project.slug.value, project.projectKey.value, {
      name: createName.value.trim(),
      color: createColor.value,
      group: createGroup.value.trim(),
      description: createDescription.value.trim(),
    })
    labels.value = [...labels.value, created]
    creating.value = false
    toast.success(`${created.name} is ready.`)
    await load()
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      createFieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
}

// ── Inline edit ─────────────────────────────────────────────────────────────────────
const editingId = ref<string | null>(null)
const savingId = ref<string | null>(null)
const editFieldErrors = ref<Record<string, string[]>>({})
const editName = ref('')
const editColor = ref('')
const editGroup = ref('')
const editDescription = ref('')

function startEdit(label: Label) {
  editingId.value = label.id
  editFieldErrors.value = {}
  editName.value = label.name
  editColor.value = label.color ?? ''
  editGroup.value = label.group ?? ''
  editDescription.value = label.description ?? ''
}

function cancelEdit() {
  editingId.value = null
}

async function saveEdit(label: Label) {
  savingId.value = label.id
  editFieldErrors.value = {}
  try {
    const updated = await updateLabel(project.slug.value, project.projectKey.value, label.id, {
      name: editName.value.trim(),
      color: editColor.value.trim(),
      group: editGroup.value.trim(),
      description: editDescription.value.trim(),
      version: label.version,
    })
    labels.value = labels.value.map((existing) => (existing.id === updated.id ? updated : existing))
    editingId.value = null
    toast.success('Saved.')
  } catch (error) {
    if (error instanceof ConflictError) {
      // Someone else saved first. Show the server's state rather than retry with a stale version.
      toast.error(error)
      editingId.value = null
      await load()
    } else if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      editFieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    savingId.value = null
  }
}

// ── Delete ──────────────────────────────────────────────────────────────────────────
const pendingDelete = ref<Label | null>(null)
const deleting = ref(false)

async function confirmDelete() {
  const label = pendingDelete.value
  if (!label) return

  deleting.value = true
  try {
    await deleteLabel(project.slug.value, project.projectKey.value, label.id)
    labels.value = labels.value.filter((existing) => existing.id !== label.id)
    pendingDelete.value = null
    toast.success(`${label.name} was deleted.`)
  } catch (error) {
    toast.error(error)
  } finally {
    deleting.value = false
  }
}
</script>

<template>
  <SettingsSection wide title="Labels" description="Tag work items with your own vocabulary — and namespace it with a group, e.g. &quot;type: frontend&quot;.">
    <div v-if="mayWrite" class="flex justify-end pb-4">
      <Button size="sm" @click="openCreate"><Plus class="size-4" aria-hidden="true" /> New label</Button>
    </div>

    <UiPageState v-if="loading" state="loading" />

    <UiPageState
      v-else-if="failed"
      state="error"
      title="Labels could not be loaded"
      description="The request did not come back. Try again."
    >
      <Button variant="secondary" @click="load">Try again</Button>
    </UiPageState>

    <EmptyState
      v-else-if="labels.length === 0"
      title="No labels yet"
      description="Create one to start tagging items — a colour and an optional group are yours to pick."
      icon="◇"
    >
      <Button v-if="mayWrite" @click="openCreate">New label</Button>
    </EmptyState>

    <div v-else class="space-y-5">
      <div v-for="section in sections" :key="section.group ?? '__ungrouped'">
        <h3 class="font-label mb-1.5">{{ section.group ?? 'Ungrouped' }}</h3>
        <ul class="border-border divide-border divide-y rounded-lg border">
          <li v-for="label in section.labels" :key="label.id" class="px-3 py-2.5">
            <div v-if="editingId !== label.id" class="flex items-center gap-3">
              <span
                class="size-2.5 flex-none rounded-full"
                :class="!label.color && 'bg-muted-foreground/50'"
                :style="label.color ? { backgroundColor: label.color } : undefined"
                aria-hidden="true"
              />
              <div class="min-w-0 flex-1">
                <div class="flex items-center gap-2">
                  <span class="truncate font-medium">{{ label.name }}</span>
                  <span v-if="label.color" class="text-muted-foreground font-mono text-[10px]">
                    {{ label.color }}
                  </span>
                </div>
                <p v-if="label.description" class="text-muted-foreground truncate text-xs">
                  {{ label.description }}
                </p>
              </div>
              <span class="text-muted-foreground flex-none text-xs">
                {{ label.itemCount }} {{ label.itemCount === 1 ? 'item' : 'items' }}
              </span>
              <Button
                v-if="mayWrite"
                variant="ghost"
                size="icon"
                :aria-label="`Edit ${label.name}`"
                @click="startEdit(label)"
              >
                <Pencil class="size-4" aria-hidden="true" />
              </Button>
              <Button
                v-if="mayDelete"
                variant="ghost"
                size="icon"
                :aria-label="`Delete ${label.name}`"
                @click="pendingDelete = label"
              >
                <Trash2 class="size-4" aria-hidden="true" />
              </Button>
            </div>

            <form
              v-else
              class="space-y-3"
              novalidate
              @submit.prevent="saveEdit(label)"
            >
              <div class="grid gap-3 sm:grid-cols-2">
                <div class="space-y-1">
                  <label :for="`label-name-${label.id}`" class="text-xs font-medium">Name</label>
                  <Input
                    :id="`label-name-${label.id}`"
                    v-model="editName"
                    required
                    :aria-invalid="Boolean(editFieldErrors.name)"
                  />
                  <p
                    v-for="message in editFieldErrors.name"
                    :key="message"
                    class="text-destructive text-xs"
                  >
                    {{ message }}
                  </p>
                </div>
                <div class="space-y-1">
                  <label :for="`label-color-${label.id}`" class="text-xs font-medium">Colour</label>
                  <div class="flex items-center gap-2">
                    <Input
                      :id="`label-color-${label.id}`"
                      v-model="editColor"
                      placeholder="#eda45c"
                      class="font-mono"
                      :aria-invalid="Boolean(editFieldErrors.color)"
                    />
                    <button
                      v-for="preset in presetLabelColors"
                      :key="preset.hex"
                      type="button"
                      class="size-4 flex-none rounded-full"
                      :style="{ backgroundColor: preset.hex }"
                      :aria-label="preset.name"
                      @click="editColor = preset.hex"
                    />
                  </div>
                  <p
                    v-for="message in editFieldErrors.color"
                    :key="message"
                    class="text-destructive text-xs"
                  >
                    {{ message }}
                  </p>
                </div>
              </div>

              <div class="space-y-1">
                <label :for="`label-group-${label.id}`" class="text-xs font-medium">Group</label>
                <Input
                  :id="`label-group-${label.id}`"
                  v-model="editGroup"
                  placeholder="type"
                  :aria-invalid="Boolean(editFieldErrors.group)"
                />
                <p
                  v-for="message in editFieldErrors.group"
                  :key="message"
                  class="text-destructive text-xs"
                >
                  {{ message }}
                </p>
              </div>

              <div class="space-y-1">
                <label :for="`label-description-${label.id}`" class="text-xs font-medium">
                  Description
                </label>
                <Textarea
                  :id="`label-description-${label.id}`"
                  v-model="editDescription"
                  rows="2"
                  :aria-invalid="Boolean(editFieldErrors.description)"
                />
                <p
                  v-for="message in editFieldErrors.description"
                  :key="message"
                  class="text-destructive text-xs"
                >
                  {{ message }}
                </p>
              </div>

              <div class="flex justify-end gap-2">
                <Button type="button" variant="ghost" :disabled="savingId === label.id" @click="cancelEdit">
                  Cancel
                </Button>
                <Button type="submit" :disabled="savingId === label.id || !editName.trim()">
                  <Loader2 v-if="savingId === label.id" class="animate-spin" aria-hidden="true" />
                  Save
                </Button>
              </div>
            </form>
          </li>
        </ul>
      </div>
    </div>

    <Dialog v-model:open="creating">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>New label</DialogTitle>
          <DialogDescription>
            A colour and a group are both optional — a group is what turns a label into
            "group: name", e.g. "type: frontend".
          </DialogDescription>
        </DialogHeader>

        <form id="create-label" class="space-y-4" novalidate @submit.prevent="submitCreate">
          <div class="space-y-1.5">
            <label for="new-label-name" class="text-sm font-medium">Name</label>
            <Input
              id="new-label-name"
              v-model="createName"
              required
              placeholder="frontend"
              :aria-invalid="Boolean(createFieldErrors.name)"
            />
            <p
              v-for="message in createFieldErrors.name"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>

          <div class="space-y-1.5">
            <label for="new-label-color" class="text-sm font-medium">Colour</label>
            <div class="flex items-center gap-2">
              <Input id="new-label-color" v-model="createColor" class="font-mono" />
              <button
                v-for="preset in presetLabelColors"
                :key="preset.hex"
                type="button"
                class="size-4 flex-none rounded-full"
                :style="{ backgroundColor: preset.hex }"
                :aria-label="preset.name"
                @click="createColor = preset.hex"
              />
            </div>
            <p
              v-for="message in createFieldErrors.color"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>

          <div class="space-y-1.5">
            <label for="new-label-group" class="text-sm font-medium">Group</label>
            <Input
              id="new-label-group"
              v-model="createGroup"
              placeholder="type"
              :aria-invalid="Boolean(createFieldErrors.group)"
            />
            <p
              v-for="message in createFieldErrors.group"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>

          <div class="space-y-1.5">
            <label for="new-label-description" class="text-sm font-medium">Description</label>
            <Textarea
              id="new-label-description"
              v-model="createDescription"
              rows="2"
              :aria-invalid="Boolean(createFieldErrors.description)"
            />
            <p
              v-for="message in createFieldErrors.description"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </div>
        </form>

        <DialogFooter>
          <Button type="button" variant="ghost" :disabled="submitting" @click="creating = false">
            Cancel
          </Button>
          <Button type="submit" form="create-label" :disabled="submitting || !createName.trim()">
            <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
            Create label
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog :open="pendingDelete !== null" @update:open="(open) => !open && (pendingDelete = null)">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Delete {{ pendingDelete?.name }}?</DialogTitle>
          <DialogDescription>
            <template v-if="pendingDelete && pendingDelete.itemCount > 0">
              It will be removed from {{ pendingDelete.itemCount }}
              {{ pendingDelete.itemCount === 1 ? 'item' : 'items' }}. This cannot be undone.
            </template>
            <template v-else>This cannot be undone.</template>
          </DialogDescription>
        </DialogHeader>

        <DialogFooter>
          <Button type="button" variant="ghost" :disabled="deleting" @click="pendingDelete = null">
            Cancel
          </Button>
          <Button variant="destructive" :disabled="deleting" @click="confirmDelete">
            <Loader2 v-if="deleting" class="animate-spin" aria-hidden="true" />
            Delete
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </SettingsSection>
</template>
