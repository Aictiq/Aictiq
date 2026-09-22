<script setup lang="ts">
import { ExternalLink, Loader2, MoreHorizontal, Star } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import {
  createPlaybook,
  createStarterPlaybook,
  deletePlaybook,
  listPlaybooks,
  playbookHarnesses,
  promotePlaybook,
  updatePlaybook,
  type Playbook,
  type PlaybookHarness,
  type SavePlaybookBody,
} from '@/api/playbooks'
import { listProjects, type Project } from '@/api/projects'
import { wikiTree, type WikiTreePage } from '@/api/wiki'
import { listWorkflows, type WorkflowState } from '@/api/workflows'
import EmptyState from '@/components/common/EmptyState.vue'
import FactoryDocsLink from '@/components/factory/FactoryDocsLink.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import WikiPageTree from '@/components/wiki/WikiPageTree.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { wikiPath } from '@/router/paths'
import { ApiError, ConflictError } from '@/utils/api'

interface ProjectPlaybooks {
  project: Project
  playbooks: Playbook[]
  states: WorkflowState[]
}

const org = useOrgScope()
const toast = useToast()
const groups = ref<ProjectPlaybooks[]>([])
const loading = ref(true)
const failed = ref(false)
const busyId = ref<string | null>(null)

const editing = ref<{ group: ProjectPlaybooks; playbook: Playbook | null } | null>(null)
const pages = ref<WikiTreePage[]>([])
const pagesLoading = ref(false)
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})
const confirmingDelete = ref<{ group: ProjectPlaybooks; playbook: Playbook } | null>(null)

const name = ref('')
const wikiPageId = ref('')
const harness = ref<PlaybookHarness>('claude')
const onSuccessStateId = ref('')
const onFailureStateId = ref('')
const maxMinutes = ref(60)

const mayEditCurrent = computed(
  () => editing.value?.group.project.role === 'admin' && !editing.value.group.project.isArchived,
)
const selectedPage = computed(() => pages.value.find((page) => page.id === wikiPageId.value))
const canSubmit = computed(
  () =>
    mayEditCurrent.value &&
    name.value.trim().length > 0 &&
    wikiPageId.value.length > 0 &&
    maxMinutes.value >= 5 &&
    maxMinutes.value <= 720,
)

async function load() {
  loading.value = true
  failed.value = false
  try {
    // The endpoint deliberately starts at Member; visible projects where this caller is only
    // a Guest are not probed one by one (and therefore cannot turn the whole roster into 403).
    const projects = (await listProjects(org.slug.value)).filter(
      (project) => project.role !== 'guest',
    )
    groups.value = await Promise.all(
      projects.map(async (project) => {
        const [playbooks, workflows] = await Promise.all([
          listPlaybooks(org.slug.value, project.key),
          listWorkflows(org.slug.value, project.key),
        ])
        return {
          project,
          playbooks,
          states: workflows.find((workflow) => workflow.isDefault)?.states ?? [],
        }
      }),
    )
  } catch (error) {
    groups.value = []
    failed.value = true
    toast.error(error)
  } finally {
    loading.value = false
  }
}

watch(() => org.slug.value, load, { immediate: true })

function replace(group: ProjectPlaybooks, saved: Playbook) {
  group.playbooks = group.playbooks
    .map((entry) => (entry.id === saved.id ? saved : entry))
    .sort((left, right) => left.name.localeCompare(right.name))
}

async function openEditor(group: ProjectPlaybooks, playbook: Playbook | null = null) {
  editing.value = { group, playbook }
  name.value = playbook?.name ?? ''
  wikiPageId.value = playbook?.wikiPageId ?? ''
  harness.value = playbook?.harness ?? 'claude'
  onSuccessStateId.value = playbook?.onSuccessStateId ?? ''
  onFailureStateId.value = playbook?.onFailureStateId ?? ''
  maxMinutes.value = playbook?.maxMinutes ?? 60
  fieldErrors.value = {}
  pages.value = []
  pagesLoading.value = true
  try {
    pages.value = await wikiTree(org.slug.value, group.project.key)
  } catch (error) {
    toast.error(error)
  } finally {
    pagesLoading.value = false
  }
}

function closeEditor() {
  if (!submitting.value) editing.value = null
}

async function save() {
  const context = editing.value
  if (!context || !canSubmit.value) return

  const body: SavePlaybookBody = {
    name: name.value.trim(),
    wikiPageId: wikiPageId.value,
    harness: harness.value,
    onSuccessStateId: onSuccessStateId.value || null,
    onFailureStateId: onFailureStateId.value || null,
    maxMinutes: maxMinutes.value,
  }

  submitting.value = true
  fieldErrors.value = {}
  try {
    const saved = context.playbook
      ? await updatePlaybook(org.slug.value, context.group.project.key, context.playbook.id, {
          ...body,
          version: context.playbook.version,
        })
      : await createPlaybook(org.slug.value, context.group.project.key, body)

    if (context.playbook) replace(context.group, saved)
    else {
      context.group.playbooks = [...context.group.playbooks, saved].sort((left, right) =>
        left.name.localeCompare(right.name),
      )
    }
    editing.value = null
    toast.success(`${saved.name} ${context.playbook ? 'updated' : 'created'}.`)
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else if (error instanceof ConflictError) {
      toast.error(new Error('Someone changed this playbook first. The list has been refreshed.'))
      editing.value = null
      await load()
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
}

async function starter(group: ProjectPlaybooks) {
  busyId.value = `starter:${group.project.id}`
  try {
    const created = await createStarterPlaybook(org.slug.value, group.project.key)
    group.playbooks = [...group.playbooks, created]
    toast.success('Starter playbook created. Open its page to tailor it to this project.')
  } catch (error) {
    if (error instanceof ConflictError) await load()
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

async function promote(group: ProjectPlaybooks, playbook: Playbook) {
  busyId.value = playbook.id
  try {
    const promoted = await promotePlaybook(org.slug.value, group.project.key, playbook.id)
    group.playbooks = group.playbooks.map((entry) => ({
      ...entry,
      isDefault: entry.id === promoted.id,
      ...(entry.id === promoted.id ? promoted : {}),
    }))
    toast.success(`${playbook.name} is now the default.`)
  } catch (error) {
    toast.error(error)
    await load()
  } finally {
    busyId.value = null
  }
}

async function remove() {
  const pending = confirmingDelete.value
  if (!pending) return
  busyId.value = pending.playbook.id
  try {
    await deletePlaybook(org.slug.value, pending.group.project.key, pending.playbook.id)
    pending.group.playbooks = pending.group.playbooks.filter(
      (entry) => entry.id !== pending.playbook.id,
    )
    confirmingDelete.value = null
    toast.success(`${pending.playbook.name} deleted. Its wiki page was kept.`)
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

function stateName(group: ProjectPlaybooks, stateId: string | null) {
  if (!stateId) return 'No transition'
  return group.states.find((state) => state.id === stateId)?.name ?? 'State unavailable'
}
</script>

<template>
  <SettingsSection wide>
    <header class="pb-3">
      <h2 class="text-sm font-medium">Playbooks</h2>
      <p class="text-muted-foreground mt-0.5 text-xs">
        Reusable instructions for an agent run. The instructions themselves stay in the wiki, where
        the team can review their history.
      </p>
    </header>

    <UiPageState v-if="loading" state="loading" />
    <UiPageState
      v-else-if="failed"
      state="error"
      title="Could not load playbooks"
      description="Try the request again."
    >
      <Button variant="secondary" @click="load">Try again</Button>
    </UiPageState>
    <EmptyState
      v-else-if="groups.length === 0"
      title="No visible projects"
      description="Playbooks belong to projects where you are at least a Member."
      icon="◇"
    >
      <FactoryDocsLink label="Learn about playbooks" />
    </EmptyState>

    <div v-else class="space-y-6">
      <section v-for="group in groups" :key="group.project.id">
        <div class="mb-2 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 class="text-sm font-medium">{{ group.project.name }}</h3>
            <p class="text-muted-foreground text-xs">{{ group.project.key }}</p>
          </div>
          <div
            v-if="group.project.role === 'admin' && !group.project.isArchived"
            class="flex gap-2"
          >
            <Button
              v-if="group.playbooks.length === 0"
              size="sm"
              variant="outline"
              :disabled="busyId !== null"
              @click="starter(group)"
            >
              <Loader2
                v-if="busyId === `starter:${group.project.id}`"
                class="animate-spin"
                aria-hidden="true"
              />
              Create starter
            </Button>
            <Button size="sm" @click="openEditor(group)">New playbook</Button>
          </div>
        </div>

        <div
          v-if="group.playbooks.length === 0"
          class="border-border text-muted-foreground rounded-lg border border-dashed p-5 text-sm"
        >
          No playbooks in this project yet.
          <span v-if="group.project.role !== 'admin'">A project Admin can create one.</span>
          <div class="mt-3">
            <FactoryDocsLink label="Learn about playbooks" />
          </div>
        </div>

        <ul v-else class="border-border divide-border overflow-hidden rounded-lg border divide-y">
          <li
            v-for="playbook in group.playbooks"
            :key="playbook.id"
            class="flex items-start gap-3 px-3 py-3"
          >
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-1.5">
                <p class="font-medium">{{ playbook.name }}</p>
                <Badge class="font-mono text-[10px]" variant="secondary">
                  {{ playbook.harness }}
                </Badge>
                <Badge v-if="playbook.isDefault" class="text-[10px]" variant="outline">
                  <Star class="size-3 fill-current" aria-hidden="true" /> Default
                </Badge>
              </div>
              <p class="text-muted-foreground mt-1 text-xs">
                Success: {{ stateName(group, playbook.onSuccessStateId) }} · Failure:
                {{ stateName(group, playbook.onFailureStateId) }} · {{ playbook.maxMinutes }} min
              </p>
              <RouterLink
                v-if="playbook.wikiPageId"
                class="text-primary mt-1 inline-flex items-center gap-1 text-xs hover:underline"
                :to="wikiPath(org.slug.value, group.project.key, playbook.wikiPageId)"
              >
                Open wiki page <ExternalLink class="size-3" aria-hidden="true" />
              </RouterLink>
              <p v-else class="text-destructive mt-1 text-xs">Backing page missing</p>
            </div>

            <Loader2
              v-if="busyId === playbook.id"
              class="text-muted-foreground size-4 animate-spin"
              aria-label="Saving"
            />
            <DropdownMenu v-else-if="group.project.role === 'admin' && !group.project.isArchived">
              <DropdownMenuTrigger as-child>
                <Button variant="ghost" size="icon" :aria-label="`Manage ${playbook.name}`">
                  <MoreHorizontal class="size-4" aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem @select="openEditor(group, playbook)">Edit</DropdownMenuItem>
                <DropdownMenuItem v-if="!playbook.isDefault" @select="promote(group, playbook)">
                  Make default
                </DropdownMenuItem>
                <DropdownMenuItem
                  variant="destructive"
                  @select="confirmingDelete = { group, playbook }"
                >
                  Delete
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </li>
        </ul>
      </section>
    </div>

    <Dialog :open="editing !== null" @update:open="(open: boolean) => !open && closeEditor()">
      <DialogContent class="max-h-[90vh] overflow-y-auto sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{{ editing?.playbook ? 'Edit playbook' : 'New playbook' }}</DialogTitle>
          <DialogDescription>
            Choose a wiki page for the instructions, then set how an agent should run them.
          </DialogDescription>
        </DialogHeader>

        <form id="playbook-form" class="space-y-4" novalidate @submit.prevent="save">
          <div class="space-y-1.5">
            <label for="playbook-name" class="text-sm font-medium">Name</label>
            <Input
              id="playbook-name"
              v-model="name"
              required
              placeholder="Implement"
              :aria-invalid="Boolean(fieldErrors.name)"
            />
            <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
              {{ message }}
            </p>
          </div>

          <fieldset class="space-y-1.5">
            <legend class="text-sm font-medium">Wiki page</legend>
            <div class="border-border max-h-44 overflow-y-auto rounded-md border p-1">
              <p v-if="pagesLoading" class="text-muted-foreground p-2 text-xs">Loading pages…</p>
              <p v-else-if="pages.length === 0" class="text-muted-foreground p-2 text-xs">
                This project has no visible wiki pages. Create one in the wiki first.
              </p>
              <WikiPageTree
                v-else
                :pages="pages"
                :selected-id="wikiPageId"
                @select="wikiPageId = $event.id"
              />
            </div>
            <p class="text-muted-foreground text-xs">
              {{
                selectedPage
                  ? `Selected: ${selectedPage.title}`
                  : 'Select the page agents should follow.'
              }}
            </p>
            <p
              v-for="message in fieldErrors.wikiPageId"
              :key="message"
              class="text-destructive text-xs"
            >
              {{ message }}
            </p>
          </fieldset>

          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label for="playbook-harness" class="text-sm font-medium">Harness</label>
              <select
                id="playbook-harness"
                v-model="harness"
                class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
              >
                <option v-for="option in playbookHarnesses" :key="option" :value="option">
                  {{ option }}
                </option>
              </select>
            </div>
            <div class="space-y-1.5">
              <label for="playbook-minutes" class="text-sm font-medium">Time limit (minutes)</label>
              <Input
                id="playbook-minutes"
                v-model.number="maxMinutes"
                type="number"
                min="5"
                max="720"
                :aria-invalid="Boolean(fieldErrors.maxMinutes)"
              />
              <p
                v-for="message in fieldErrors.maxMinutes"
                :key="message"
                class="text-destructive text-xs"
              >
                {{ message }}
              </p>
            </div>
          </div>

          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label for="playbook-success" class="text-sm font-medium">On success</label>
              <select
                id="playbook-success"
                v-model="onSuccessStateId"
                class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
              >
                <option value="">Do not move the item</option>
                <option
                  v-for="state in editing?.group.states ?? []"
                  :key="state.id"
                  :value="state.id"
                >
                  {{ state.name }}
                </option>
              </select>
            </div>
            <div class="space-y-1.5">
              <label for="playbook-failure" class="text-sm font-medium">On failure</label>
              <select
                id="playbook-failure"
                v-model="onFailureStateId"
                class="border-input bg-background h-9 w-full rounded-md border px-2 text-sm"
              >
                <option value="">Do not move the item</option>
                <option
                  v-for="state in editing?.group.states ?? []"
                  :key="state.id"
                  :value="state.id"
                >
                  {{ state.name }}
                </option>
              </select>
            </div>
          </div>
        </form>

        <DialogFooter>
          <Button variant="ghost" :disabled="submitting" @click="closeEditor">Cancel</Button>
          <Button type="submit" form="playbook-form" :disabled="submitting || !canSubmit">
            <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
            {{ editing?.playbook ? 'Save changes' : 'Create playbook' }}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog
      :open="confirmingDelete !== null"
      @update:open="
        (open: boolean) => {
          if (!open) confirmingDelete = null
        }
      "
    >
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Delete {{ confirmingDelete?.playbook.name }}?</DialogTitle>
          <DialogDescription>
            The playbook settings are deleted. Its wiki page and revision history are kept.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" @click="confirmingDelete = null">Cancel</Button>
          <Button variant="destructive" :disabled="busyId !== null" @click="remove">Delete</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </SettingsSection>
</template>
