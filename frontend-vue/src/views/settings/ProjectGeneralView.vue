<script setup lang="ts">
import { Loader2, Trash2 } from '@lucide/vue'
import { useQueryClient } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import {
  deleteProject,
  hasProjectRole,
  setProjectArchived,
  updateProject,
  type Project,
  type ProjectVisibility,
} from '@/api/projects'
import DeleteConfirmDialog from '@/components/common/DeleteConfirmDialog.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useDirtyGuard } from '@/composables/useDirtyGuard'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { useProjectsStore } from '@/stores/projects'
import { ApiError, ConflictError } from '@/utils/api'

/**
 * What the project *is*: its name, how it is described, who can see it, and the way to
 * retire it.
 *
 * Archiving is read-only rather than hidden, so this tab keeps rendering every field
 * while the project is archived and disables all of them — the restore button beside them
 * is the way back, and a page that hid the settings would leave no obvious one.
 */
const project = useProjectScope()
const toast = useToast()

/**
 * The layout mounts a tab only once the project has loaded, and unmounts it again while a
 * reload is in flight, so the scope's record is never null while this form is on screen.
 */
const record = ref(project.record.value!)

const saving = ref(false)
const archiving = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

const name = ref('')
const description = ref('')
const visibility = ref<ProjectVisibility>('organization')
const icon = ref('')
const color = ref('')

const isAdmin = computed(() => hasProjectRole(record.value.role, 'admin'))
const isArchived = computed(() => record.value.isArchived)
/** Reading is open to anyone who can see the project; changing anything is an admin's. */
const readOnly = computed(() => !isAdmin.value || isArchived.value)

function fill(loaded: Project) {
  record.value = loaded
  name.value = loaded.name
  description.value = loaded.description ?? ''
  visibility.value = loaded.visibility
  icon.value = loaded.icon ?? ''
  color.value = loaded.color ?? ''
}

// A save, a conflict reload, or a project switched under the hub all arrive the same way:
// the scope publishes a record and the form is refilled from it.
watch(project.record, (loaded) => loaded && fill(loaded), { immediate: true })

useDirtyGuard(
  () =>
    isAdmin.value &&
    (name.value !== record.value.name ||
      description.value !== (record.value.description ?? '') ||
      visibility.value !== record.value.visibility ||
      icon.value !== (record.value.icon ?? '') ||
      color.value !== (record.value.color ?? '')),
)

async function save() {
  saving.value = true
  fieldErrors.value = {}

  try {
    project.set(
      await updateProject(project.slug.value, record.value.key, {
        name: name.value.trim(),
        // Empty means "clear it" to the API, which is exactly what an emptied field means.
        description: description.value.trim(),
        visibility: visibility.value,
        icon: icon.value.trim(),
        color: color.value.trim(),
        version: record.value.version,
      }),
    )
    toast.success('Saved.')
  } catch (error) {
    if (error instanceof ConflictError) {
      // Someone else got there first. Show what they saved rather than retrying with a
      // version the server has already moved past.
      toast.error(error)
      await project.reload()
    } else if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    saving.value = false
  }
}

// ── Delete ──────────────────────────────────────────────────────────────────────────
// Archiving is the reversible way to retire a project; this is the other one. The dialog
// names everything that goes, because none of it can be brought back.
const router = useRouter()
const client = useQueryClient()
const projects = useProjectsStore()
const deleteOpen = ref(false)
const deleting = ref(false)
const deleteError = ref<string | null>(null)
const deleteConsequences = [
  'every work item, with its comments, history, links and attachments',
  'every wiki page and its revisions',
  'its teams, sprints and boards, workflows, labels, templates and saved views',
  'its dashboards and metrics, notifications, webhooks and GitHub repository bindings',
  'everyone’s membership in it (their accounts stay)',
]

function openDelete() {
  deleteError.value = null
  deleteOpen.value = true
}

async function destroy() {
  deleting.value = true
  deleteError.value = null
  const { key, name: deleted } = record.value
  try {
    await deleteProject(project.slug.value, key, deleted)
    deleteOpen.value = false
    projects.remove(key)
    // Everything cached under the project is gone on the server; nothing should render it.
    client.removeQueries({ predicate: (query) => query.queryKey.includes(key) })
    toast.success(`${deleted} was deleted.`)
    await router.replace({ name: 'projects' })
  } catch (error) {
    if (error instanceof ApiError)
      deleteError.value =
        Object.values(error.fieldErrors).flat()[0] ?? error.problem?.detail ?? error.title
    else toast.error(error)
  } finally {
    deleting.value = false
  }
}

async function toggleArchive() {
  archiving.value = true
  const wasArchived = record.value.isArchived
  try {
    const updated = await setProjectArchived(project.slug.value, record.value.key, !wasArchived)
    project.set(updated)
    toast.success(
      wasArchived ? `${updated.name} is active again.` : `${updated.name} was archived.`,
    )
  } catch (error) {
    toast.error(error)
  } finally {
    archiving.value = false
  }
}
</script>

<template>
  <div class="space-y-8">
    <SettingsSection title="Details" description="How this project is named and described.">
      <form class="space-y-5" novalidate @submit.prevent="save">
        <div class="space-y-1.5">
          <label for="project-name" class="text-sm font-medium">Name</label>
          <Input
            id="project-name"
            v-model="name"
            required
            :disabled="readOnly"
            :aria-invalid="Boolean(fieldErrors.name)"
          />
          <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="project-key" class="text-sm font-medium">Key</label>
          <Input id="project-key" :model-value="record.key" class="font-mono" readonly disabled />
          <p class="text-muted-foreground text-xs">
            Permanent. It starts every item id your team quotes — in commits, in chat, and in every
            agent's configuration.
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="project-description" class="text-sm font-medium">Description</label>
          <textarea
            id="project-description"
            v-model="description"
            rows="3"
            :disabled="readOnly"
            class="border-border bg-background focus-visible:ring-ring w-full rounded-lg border px-2.5 py-2 text-sm focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50"
          ></textarea>
        </div>

        <div class="grid gap-4 sm:grid-cols-2">
          <div class="space-y-1.5">
            <label for="project-icon" class="text-sm font-medium">Icon</label>
            <Input
              id="project-icon"
              v-model="icon"
              placeholder="🌐"
              :disabled="readOnly"
              :aria-invalid="Boolean(fieldErrors.icon)"
            />
            <p v-for="message in fieldErrors.icon" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </div>
          <div class="space-y-1.5">
            <label for="project-color" class="text-sm font-medium">Colour</label>
            <Input
              id="project-color"
              v-model="color"
              placeholder="#4f46e5"
              class="font-mono"
              :disabled="readOnly"
              :aria-invalid="Boolean(fieldErrors.color)"
            />
            <p v-for="message in fieldErrors.color" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </div>
        </div>

        <div class="space-y-1.5">
          <label for="project-visibility" class="text-sm font-medium">Who can see it</label>
          <select
            id="project-visibility"
            v-model="visibility"
            :disabled="readOnly"
            class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm focus-visible:ring-2 focus-visible:outline-none disabled:opacity-50"
          >
            <option value="organization">Everyone in the organization</option>
            <option value="private">Only invited people</option>
          </select>
          <p class="text-muted-foreground text-xs">
            Owners and admins of the organization can always open it, whichever you pick. Changing
            this changes who is on the project without anyone being added.
          </p>
        </div>

        <div v-if="!readOnly" class="flex justify-end">
          <Button type="submit" :disabled="saving || !name.trim()">
            <Loader2 v-if="saving" class="animate-spin" aria-hidden="true" />
            Save changes
          </Button>
        </div>
        <p v-else-if="!isAdmin" class="text-muted-foreground text-xs">
          You are {{ record.role === 'guest' ? 'a guest' : 'a member' }} here. Only project admins
          can change these.
        </p>
      </form>
    </SettingsSection>

    <SettingsSection v-if="isAdmin">
      <section class="border-destructive/30 rounded-lg border p-4">
        <h2 class="text-destructive text-sm font-medium">
          {{ isArchived ? 'Restore project' : 'Archive project' }}
        </h2>
        <p class="text-muted-foreground mt-1 text-xs">
          {{
            isArchived
              ? 'It becomes writable again and returns to the project list.'
              : 'Everything stays readable and searchable; nothing can be changed until it is restored. Nothing is deleted.'
          }}
        </p>
        <div class="mt-3 flex justify-end">
          <Button
            :variant="isArchived ? 'outline' : 'destructive'"
            :disabled="archiving"
            @click="toggleArchive"
          >
            <Loader2 v-if="archiving" class="animate-spin" aria-hidden="true" />
            {{ isArchived ? 'Restore' : 'Archive' }}
          </Button>
        </div>
      </section>

      <section class="border-destructive/30 mt-4 rounded-lg border p-4">
        <h2 class="text-destructive text-sm font-medium">Delete project</h2>
        <p class="text-muted-foreground mt-1 text-xs">
          Permanently deletes {{ record.name }} and everything in it — items, wiki, files, sprints,
          integrations and metrics. There is no restore; archive instead if you may need it again.
        </p>
        <div class="mt-3 flex justify-end">
          <Button variant="destructive" @click="openDelete">
            <Trash2 aria-hidden="true" />
            Delete project
          </Button>
        </div>
      </section>
    </SettingsSection>

    <DeleteConfirmDialog
      v-model:open="deleteOpen"
      :name="record.name"
      :summary="`Deleting removes ${record.name} (${record.key}), along with:`"
      :consequences="deleteConsequences"
      confirm-label="Delete project"
      :pending="deleting"
      :error="deleteError"
      @confirm="destroy"
    />
  </div>
</template>
