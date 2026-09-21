<script setup lang="ts">
import { FilePlus, Loader2, Lock, Plus, RotateCcw, Trash2 } from '@lucide/vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, onBeforeUnmount, ref, watch } from 'vue'

import {
  attachmentContentTypes,
  attachmentUrl,
  commitAttachment,
  uploadAttachment,
} from '@/api/attachments'
import { listMembers, type Member } from '@/api/members'
import { avatarUrl } from '@/api/profile'
import { projectRoles, type ProjectRole } from '@/api/projects'
import { listTeams } from '@/api/teams'
import {
  createWikiPage,
  deleteWikiPage,
  getWikiPage,
  replaceWikiPermissions,
  restoreWikiRevision,
  updateWikiPage,
  wikiDiff,
  wikiPermissions,
  wikiRevision,
  wikiRevisions,
  wikiTree,
  type WikiPermission,
  type WikiRevision,
} from '@/api/wiki'
import DeleteConfirmDialog from '@/components/common/DeleteConfirmDialog.vue'
import Markdown from '@/components/common/Markdown.vue'
import MarkdownEditor from '@/components/common/MarkdownEditor.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import UserSelect, { type UserOption } from '@/components/common/UserSelect.vue'
import AppShell from '@/components/shell/AppShell.vue'
import WikiPageTree from '@/components/wiki/WikiPageTree.vue'
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
import { useShortcut } from '@/composables/useShortcuts'
import { useToast } from '@/composables/useToast'
import { since } from '@/lib/claims'
import { wikiOutline } from '@/lib/wiki'
import { ConflictError } from '@/utils/api'

/**
 * A local draft is keyed by page id and never leaves the browser — it exists purely to
 * survive an accidental reload or a closed tab while someone is mid-edit. Saving,
 * cancelling or explicitly discarding all remove it; nothing else does.
 */
interface WikiDraft {
  title: string
  markdown: string
  summary: string
  savedAt: string
}

const draftKey = (pageId: string) => `aictiq:wiki-draft:${pageId}`

function loadDraft(pageId: string): WikiDraft | null {
  try {
    const raw = localStorage.getItem(draftKey(pageId))
    return raw ? (JSON.parse(raw) as WikiDraft) : null
  } catch {
    return null
  }
}

function saveDraft(pageId: string, draft: WikiDraft) {
  try {
    localStorage.setItem(draftKey(pageId), JSON.stringify(draft))
  } catch {
    // Storage unavailable or full — the draft is a convenience, not a guarantee.
  }
}

function clearDraft(pageId: string) {
  try {
    localStorage.removeItem(draftKey(pageId))
  } catch {
    // Nothing to do if storage refuses the removal too.
  }
}

const props = defineProps<{ slug: string; projectKey: string; pageId?: string }>()
const client = useQueryClient()
const toast = useToast()

const selectedId = ref(props.pageId ?? '')
const editing = ref(false)
const title = ref('')
const markdown = ref('')
const summary = ref('')
const conflict = ref(false)
const recoveredDraft = ref<WikiDraft | null>(null)

const tree = useQuery({
  queryKey: computed(() => ['wiki-tree', props.slug, props.projectKey]),
  queryFn: () => wikiTree(props.slug, props.projectKey),
})
const page = useQuery({
  queryKey: computed(() => ['wiki-page', props.slug, selectedId.value]),
  enabled: computed(() => !!selectedId.value),
  queryFn: () => getWikiPage(props.slug, selectedId.value),
})
const revisions = useQuery({
  queryKey: computed(() => ['wiki-revisions', props.slug, selectedId.value]),
  enabled: computed(() => !!selectedId.value),
  queryFn: () => wikiRevisions(props.slug, selectedId.value),
})

const current = computed(() => page.data.value ?? null)
const revisionList = computed(() => revisions.data.value ?? [])
const outline = computed(() => wikiOutline(tree.data.value ?? []))

watch(
  () => props.pageId,
  (id) => {
    if (id) selectedId.value = id
  },
)

watch(
  page.data,
  (value) => {
    if (!value || editing.value) return
    title.value = value.title
    markdown.value = value.contentMarkdown
    const draft = loadDraft(value.id)
    recoveredDraft.value =
      draft && (draft.title !== value.title || draft.markdown !== value.contentMarkdown)
        ? draft
        : null
  },
  { immediate: true },
)

function select(id: string) {
  selectedId.value = id
  editing.value = false
  conflict.value = false
}

// ── Edit / save / cancel ────────────────────────────────────────────────────────────
let draftTimer: ReturnType<typeof setTimeout> | undefined

watch([title, markdown, summary], () => {
  if (!editing.value || !selectedId.value) return
  clearTimeout(draftTimer)
  draftTimer = setTimeout(() => {
    saveDraft(selectedId.value, {
      title: title.value,
      markdown: markdown.value,
      summary: summary.value,
      savedAt: new Date().toISOString(),
    })
  }, 500)
})
onBeforeUnmount(() => clearTimeout(draftTimer))

function beginEdit() {
  if (!page.data.value) return
  title.value = page.data.value.title
  markdown.value = page.data.value.contentMarkdown
  summary.value = ''
  conflict.value = false
  recoveredDraft.value = null
  editing.value = true
}

function resumeDraft() {
  if (!recoveredDraft.value) return
  title.value = recoveredDraft.value.title
  markdown.value = recoveredDraft.value.markdown
  summary.value = recoveredDraft.value.summary
  conflict.value = false
  editing.value = true
  recoveredDraft.value = null
}

function discardDraft() {
  if (selectedId.value) clearDraft(selectedId.value)
  recoveredDraft.value = null
}

function cancelEdit() {
  if (selectedId.value) clearDraft(selectedId.value)
  if (page.data.value) {
    title.value = page.data.value.title
    markdown.value = page.data.value.contentMarkdown
  }
  summary.value = ''
  conflict.value = false
  editing.value = false
}

const save = useMutation({
  mutationFn: () =>
    updateWikiPage(props.slug, selectedId.value, {
      title: title.value,
      contentMd: markdown.value,
      version: page.data.value!.version,
      summary: summary.value || undefined,
    }),
  onSuccess: async (value) => {
    await client.invalidateQueries({ queryKey: ['wiki-tree', props.slug, props.projectKey] })
    await client.invalidateQueries({ queryKey: ['wiki-revisions', props.slug, selectedId.value] })
    client.setQueryData(['wiki-page', props.slug, selectedId.value], value)
    clearDraft(selectedId.value)
    editing.value = false
    summary.value = ''
    conflict.value = false
  },
  onError: (error) => {
    conflict.value = error instanceof ConflictError
    if (!conflict.value) toast.error(error)
  },
})

/** `parentId` makes the new page a subpage; without it the page lands at the top level. */
const create = useMutation({
  mutationFn: (parentId: string | null) =>
    createWikiPage(props.slug, props.projectKey, {
      parentId,
      title: 'Untitled page',
      contentMd: '',
    }),
  onSuccess: async (value) => {
    await client.invalidateQueries({ queryKey: ['wiki-tree', props.slug, props.projectKey] })
    // Seed the cache so the editor opens on the new page's content rather than on whatever
    // page was selected before — the page watcher ignores data while editing.
    client.setQueryData(['wiki-page', props.slug, value.id], value)
    select(value.id)
    title.value = value.title
    markdown.value = value.contentMarkdown
    summary.value = ''
    recoveredDraft.value = null
    editing.value = true
  },
  onError: (error) => toast.error(error),
})

// The page exists while it is edited, so a file is committed to it straight away — like an
// item's description. A revision may keep referring to it after the text changes again.
const acceptedFiles = attachmentContentTypes.join(',')
async function uploadToPage(file: File) {
  const id = await uploadAttachment(props.slug, props.projectKey, file)
  await commitAttachment(props.slug, id, { pageId: selectedId.value })
  return attachmentUrl(props.slug, id)
}

useShortcut('e', () => {
  if (current.value && !editing.value) beginEdit()
})
useShortcut(
  'mod+s',
  () => {
    if (editing.value) save.mutate()
  },
  { allowInInput: true },
)
useShortcut(
  'escape',
  () => {
    if (editing.value) cancelEdit()
  },
  { allowInInput: true },
)

// ── Delete ──────────────────────────────────────────────────────────────────────────
// Deleting is permanent and takes every subpage with it, so the dialog lists exactly what
// goes and asks for the page title — the same confirmation as deleting an organization.
const deleteOpen = ref(false)

/** The selected page's subpages as the tree shows them, each with its depth below it. */
const doomedSubpages = computed(() => {
  const rows = outline.value
  const index = rows.findIndex((row) => row.page.id === selectedId.value)
  if (index < 0) return []
  const base = rows[index]!.depth
  const below: { id: string; title: string; depth: number }[] = []
  for (const row of rows.slice(index + 1)) {
    if (row.depth <= base) break
    below.push({ id: row.page.id, title: row.page.title, depth: row.depth - base - 1 })
  }
  return below
})
const deleteConsequences = computed(() => {
  const plural = doomedSubpages.value.length > 0
  return [
    `every revision in ${plural ? 'their' : 'its'} history`,
    `every image and file attached to ${plural ? 'them' : 'it'}`,
    `${plural ? 'their' : 'its'} permission rules`,
  ]
})

function openDelete() {
  deleteOpen.value = true
}

const remove = useMutation({
  mutationFn: () => deleteWikiPage(props.slug, selectedId.value),
  onSuccess: async () => {
    const deleted = current.value
    for (const id of [selectedId.value, ...doomedSubpages.value.map((page) => page.id)])
      clearDraft(id)
    deleteOpen.value = false
    selectedId.value = deleted?.parentId ?? ''
    editing.value = false
    await client.invalidateQueries({ queryKey: ['wiki-tree', props.slug, props.projectKey] })
    if (deleted) client.removeQueries({ queryKey: ['wiki-page', props.slug, deleted.id] })
    toast.success(`Deleted “${deleted?.title ?? 'page'}”.`)
  },
  onError: (error) => toast.error(error),
})

// ── History / restore ───────────────────────────────────────────────────────────────
const pendingRestore = ref<WikiRevision | null>(null)

/** What a revision did, in words: its summary when the author left one. */
function revisionLabel(revision: WikiRevision): string {
  if (revision.summary) return revision.summary
  return revision.number === 1 ? 'Created the page' : 'Edited the page'
}

/** A revision opens as its diff against the one before; the first has nothing before it. */
const viewing = ref<WikiRevision | null>(null)
const diff = useQuery({
  queryKey: computed(() => ['wiki-diff', props.slug, selectedId.value, viewing.value?.number]),
  enabled: computed(() => !!viewing.value && viewing.value.number > 1),
  queryFn: () =>
    wikiDiff(props.slug, selectedId.value, viewing.value!.number - 1, viewing.value!.number),
})
const firstRevision = useQuery({
  queryKey: computed(() => ['wiki-revision', props.slug, selectedId.value, 1]),
  enabled: computed(() => viewing.value?.number === 1),
  queryFn: () => wikiRevision(props.slug, selectedId.value, 1),
})
const diffLines = computed(() => diff.data.value?.hunks.flatMap((hunk) => hunk.lines) ?? [])
const diffLoading = computed(() =>
  viewing.value?.number === 1 ? firstRevision.isLoading.value : diff.isLoading.value,
)

function restoreViewed() {
  pendingRestore.value = viewing.value
  viewing.value = null
}

const restore = useMutation({
  mutationFn: (number: number) => restoreWikiRevision(props.slug, selectedId.value, number),
  onSuccess: async (value) => {
    await client.invalidateQueries({ queryKey: ['wiki-tree', props.slug, props.projectKey] })
    await client.invalidateQueries({ queryKey: ['wiki-revisions', props.slug, selectedId.value] })
    client.setQueryData(['wiki-page', props.slug, selectedId.value], value)
    clearDraft(selectedId.value)
    editing.value = false
    pendingRestore.value = null
    toast.success(`Restored revision #${value.revisionNumber - 1}.`)
  },
  onError: (error) => {
    toast.error(error)
    pendingRestore.value = null
  },
})

function confirmRestore() {
  if (pendingRestore.value) restore.mutate(pendingRestore.value.number)
}

// ── Permissions ──────────────────────────────────────────────────────────────────────
const permissionsOpen = ref(false)
const rules = ref<WikiPermission[]>([])
const savingPermissions = ref(false)
const newKind = ref<WikiPermission['subjectKind']>('user')
const newUserId = ref<string | null>(null)
const newTeamId = ref('')
const newRole = ref<ProjectRole>('member')
const newAccess = ref<WikiPermission['access']>('read')

const membersQuery = useQuery({
  queryKey: computed(() => ['members', props.slug]),
  enabled: permissionsOpen,
  queryFn: () => listMembers(props.slug, { pageSize: 100 }),
})
const teamsQuery = useQuery({
  queryKey: computed(() => ['teams', props.slug, props.projectKey]),
  enabled: permissionsOpen,
  queryFn: () => listTeams(props.slug, props.projectKey),
})
const permissionsQuery = useQuery({
  queryKey: computed(() => ['wiki-permissions', props.slug, selectedId.value]),
  enabled: computed(() => permissionsOpen.value && !!selectedId.value),
  queryFn: () => wikiPermissions(props.slug, selectedId.value),
})

const userOptions = computed<UserOption[]>(() =>
  (membersQuery.data.value?.items ?? []).map((member: Member) => ({
    userId: member.userId,
    displayName: member.displayName,
    email: member.email,
    avatarKey: member.avatarKey,
    isAgent: member.isAgent,
  })),
)

watch(permissionsQuery.data, (data) => {
  if (data) rules.value = data.map((rule) => ({ ...rule }))
})

function openPermissions() {
  if (!selectedId.value) return
  newKind.value = 'user'
  newUserId.value = null
  newTeamId.value = ''
  newRole.value = 'member'
  newAccess.value = 'read'
  permissionsOpen.value = true
}

function subjectLabel(rule: WikiPermission): string {
  if (rule.subjectKind === 'user') {
    return (
      userOptions.value.find((user) => user.userId === rule.subjectId)?.displayName ??
      rule.subjectId
    )
  }
  if (rule.subjectKind === 'team') {
    return teamsQuery.data.value?.find((team) => team.id === rule.subjectId)?.name ?? rule.subjectId
  }
  return `${rule.subjectId} (project role)`
}

const canAddRule = computed(() => {
  if (newKind.value === 'user') return !!newUserId.value
  if (newKind.value === 'team') return !!newTeamId.value
  return true
})

function addRule() {
  if (!canAddRule.value) return
  const subjectId =
    newKind.value === 'user'
      ? newUserId.value!
      : newKind.value === 'team'
        ? newTeamId.value
        : newRole.value
  const withoutExisting = rules.value.filter(
    (rule) => !(rule.subjectKind === newKind.value && rule.subjectId === subjectId),
  )
  rules.value = [
    ...withoutExisting,
    { subjectKind: newKind.value, subjectId, access: newAccess.value },
  ]
  newUserId.value = null
  newTeamId.value = ''
  newRole.value = 'member'
  newAccess.value = 'read'
}

function removeRule(rule: WikiPermission) {
  rules.value = rules.value.filter(
    (existing) =>
      !(existing.subjectKind === rule.subjectKind && existing.subjectId === rule.subjectId),
  )
}

function setRuleAccess(rule: WikiPermission, access: WikiPermission['access']) {
  rules.value = rules.value.map((existing) =>
    existing.subjectKind === rule.subjectKind && existing.subjectId === rule.subjectId
      ? { ...existing, access }
      : existing,
  )
}

async function savePermissions() {
  if (!selectedId.value) return
  savingPermissions.value = true
  try {
    await replaceWikiPermissions(props.slug, selectedId.value, rules.value)
    await client.invalidateQueries({ queryKey: ['wiki-permissions', props.slug, selectedId.value] })
    toast.success('Permissions updated.')
    permissionsOpen.value = false
  } catch (error) {
    toast.error(error)
  } finally {
    savingPermissions.value = false
  }
}
</script>

<template>
  <AppShell>
    <main
      class="grid grid-cols-1 md:min-h-[calc(100vh-4rem)] md:grid-cols-[16rem_minmax(0,1fr)_14rem]"
    >
      <aside
        class="max-h-[40vh] overflow-y-auto border-b p-4 md:max-h-none md:border-r md:border-b-0"
      >
        <div class="mb-3 flex items-center justify-between">
          <h1 class="font-semibold">Wiki</h1>
          <Button
            size="sm"
            variant="outline"
            :disabled="create.isPending.value"
            @click="create.mutate(null)"
          >
            <Loader2
              v-if="create.isPending.value"
              class="size-3.5 animate-spin"
              aria-hidden="true"
            />
            <Plus v-else class="size-3.5" aria-hidden="true" />
            New
          </Button>
        </div>
        <WikiPageTree
          :pages="tree.data.value ?? []"
          :selected-id="selectedId"
          @select="select($event.id)"
        >
          <template #actions="{ page: entry }">
            <button
              class="text-muted-foreground hover:text-foreground mr-1 flex-none rounded p-0.5 opacity-0 group-hover:opacity-100 focus-visible:opacity-100 disabled:opacity-50"
              :aria-label="`Add subpage to ${entry.title}`"
              :title="`Add subpage to ${entry.title}`"
              :disabled="create.isPending.value"
              @click="create.mutate(entry.id)"
            >
              <Plus class="size-3.5" aria-hidden="true" />
            </button>
          </template>
        </WikiPageTree>
      </aside>

      <section class="min-w-0 p-4 sm:p-6">
        <p v-if="page.isLoading.value" class="text-muted-foreground">Loading page…</p>

        <template v-else-if="current">
          <div class="mb-4 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
            <input
              v-if="editing"
              v-model="title"
              class="w-full border-b bg-transparent text-2xl font-semibold outline-none"
            />
            <h2 v-else class="text-2xl font-semibold">{{ current.title }}</h2>

            <div v-if="!editing" class="flex flex-none flex-wrap gap-2">
              <Button variant="outline" size="sm" @click="openPermissions">
                <Lock class="size-3.5" aria-hidden="true" />
                Permissions
              </Button>
              <Button
                variant="outline"
                size="sm"
                :disabled="create.isPending.value"
                @click="create.mutate(current.id)"
              >
                <FilePlus class="size-3.5" aria-hidden="true" />
                Add subpage
              </Button>
              <Button variant="destructive" size="sm" @click="openDelete">
                <Trash2 class="size-3.5" aria-hidden="true" />
                Delete
              </Button>
              <Button size="sm" @click="beginEdit">Edit</Button>
            </div>
          </div>

          <div
            v-if="recoveredDraft && !editing"
            class="mb-4 flex flex-col gap-3 rounded border border-amber-400/50 bg-amber-400/10 px-3 py-2 text-sm sm:flex-row sm:items-center sm:justify-between"
          >
            <span
              >You have unsaved local changes from
              {{ new Date(recoveredDraft.savedAt).toLocaleString() }}.</span
            >
            <div class="flex flex-none gap-2">
              <Button size="sm" variant="outline" @click="discardDraft">Discard</Button>
              <Button size="sm" @click="resumeDraft">Resume editing</Button>
            </div>
          </div>

          <div v-if="editing">
            <MarkdownEditor v-model="markdown" :upload="uploadToPage" :accept="acceptedFiles" />
            <Input v-model="summary" class="mt-3" placeholder="Save summary (optional)" />
            <p v-if="conflict" class="mt-2 text-sm text-destructive">
              Someone saved first. Refresh the page, merge your draft, then save again.
            </p>
            <div class="mt-3 flex gap-2">
              <Button :disabled="save.isPending.value" @click="save.mutate()">
                <Loader2
                  v-if="save.isPending.value"
                  class="size-3.5 animate-spin"
                  aria-hidden="true"
                />
                Save
              </Button>
              <Button variant="ghost" :disabled="save.isPending.value" @click="cancelEdit"
                >Cancel</Button
              >
            </div>
          </div>
          <Markdown v-else :source="current.contentMarkdown" />
        </template>

        <p v-else class="text-muted-foreground">Select a page or create the first one.</p>
      </section>

      <aside class="border-t p-4 md:border-t-0 md:border-l">
        <h2 class="text-sm font-semibold">History</h2>
        <ol class="mt-3 space-y-1">
          <li v-for="revision in revisionList" :key="revision.number">
            <button
              class="hover:bg-muted flex w-full gap-2 rounded p-1.5 text-left"
              :aria-label="`View revision ${revision.number}`"
              @click="viewing = revision"
            >
              <UserAvatar
                size="sm"
                class="mt-0.5"
                :name="revision.author.displayName"
                :is-agent="revision.author.isAgent"
                :src="avatarUrl(revision.author.id, revision.author.avatarKey)"
              />
              <span class="min-w-0 flex-1 text-xs">
                <span class="flex items-baseline justify-between gap-1">
                  <span class="truncate font-medium">{{ revision.author.displayName }}</span>
                  <span class="text-muted-foreground flex-none">#{{ revision.number }}</span>
                </span>
                <span class="text-foreground/80 line-clamp-2 block">{{
                  revisionLabel(revision)
                }}</span>
                <span class="text-muted-foreground mt-0.5 flex flex-wrap items-center gap-x-1.5">
                  <time :datetime="revision.at" :title="new Date(revision.at).toLocaleString()">{{
                    since(revision.at)
                  }}</time>
                  <span
                    v-if="revision.sizeDelta !== 0"
                    :class="revision.sizeDelta > 0 ? 'text-success' : 'text-destructive'"
                    :title="`${Math.abs(revision.sizeDelta)} characters ${revision.sizeDelta > 0 ? 'added' : 'removed'}`"
                    >{{ revision.sizeDelta > 0 ? '+' : '−'
                    }}{{ Math.abs(revision.sizeDelta) }}</span
                  >
                  <span
                    v-if="revision.isCurrent"
                    class="bg-muted rounded px-1 text-[10px] font-medium"
                    >Current</span
                  >
                </span>
              </span>
            </button>
          </li>
        </ol>
      </aside>
    </main>

    <DeleteConfirmDialog
      v-model:open="deleteOpen"
      :name="current?.title ?? ''"
      :summary="`Deleting removes this page${doomedSubpages.length === 0 ? '' : ` and its ${doomedSubpages.length} ${doomedSubpages.length === 1 ? 'subpage' : 'subpages'}`}, along with:`"
      :consequences="deleteConsequences"
      :confirm-label="
        doomedSubpages.length === 0 ? 'Delete page' : `Delete ${doomedSubpages.length + 1} pages`
      "
      :pending="remove.isPending.value"
      @confirm="remove.mutate()"
    >
      <template v-if="doomedSubpages.length > 0" #details>
        <div>
          <p class="mb-1.5 text-sm font-medium">Subpages that will be deleted</p>
          <ul class="max-h-48 overflow-auto rounded-md border py-1 text-sm">
            <li class="text-muted-foreground truncate px-3 py-0.5">{{ current?.title }}</li>
            <li
              v-for="subpage in doomedSubpages"
              :key="subpage.id"
              class="truncate py-0.5 pr-3"
              :style="{ paddingLeft: `${1.75 + subpage.depth * 1}rem` }"
            >
              {{ subpage.title }}
            </li>
          </ul>
        </div>
      </template>
    </DeleteConfirmDialog>

    <Dialog :open="viewing !== null" @update:open="(open) => !open && (viewing = null)">
      <DialogContent class="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle
            >Revision #{{ viewing?.number }} · {{ viewing && revisionLabel(viewing) }}</DialogTitle
          >
          <DialogDescription v-if="viewing">
            {{ viewing.author.displayName }} · {{ new Date(viewing.at).toLocaleString() }}
            <template v-if="viewing.number > 1">
              · changes since #{{ viewing.number - 1 }}</template
            >
          </DialogDescription>
        </DialogHeader>

        <div class="max-h-[60vh] overflow-auto rounded-md border">
          <p v-if="diffLoading" class="text-muted-foreground p-3 text-sm">Loading…</p>
          <Markdown
            v-else-if="viewing?.number === 1 && firstRevision.data.value"
            class="p-3"
            :source="firstRevision.data.value.contentMarkdown"
          />
          <p
            v-else-if="
              viewing?.number !== 1 && !diffLines.some((line) => line.kind !== 'unchanged')
            "
            class="text-muted-foreground p-3 text-sm"
          >
            The content did not change — only the title or nothing at all.
          </p>
          <pre v-else class="font-mono text-xs leading-5"><div
            v-for="(line, index) in diffLines"
            :key="index"
            class="flex"
            :class="{ 'bg-success/15': line.kind === 'inserted', 'bg-destructive/15': line.kind === 'deleted' }"
          ><span class="text-muted-foreground w-10 flex-none pr-2 text-right select-none">{{ line.newLine ?? line.oldLine }}</span><span class="w-4 flex-none select-none" :class="{ 'text-success': line.kind === 'inserted', 'text-destructive': line.kind === 'deleted' }">{{ line.kind === 'inserted' ? '+' : line.kind === 'deleted' ? '−' : '' }}</span><span class="min-w-0 pr-3 whitespace-pre-wrap break-words">{{ line.text || ' ' }}</span></div></pre>
        </div>

        <DialogFooter>
          <Button variant="ghost" @click="viewing = null">Close</Button>
          <Button v-if="viewing && !viewing.isCurrent" variant="outline" @click="restoreViewed">
            <RotateCcw class="size-3.5" aria-hidden="true" />
            Restore this version
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog
      :open="pendingRestore !== null"
      @update:open="(open) => !open && (pendingRestore = null)"
    >
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Restore revision #{{ pendingRestore?.number }}?</DialogTitle>
          <DialogDescription>
            This creates a new revision with that content — nothing is lost, the current version
            stays in history.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" :disabled="restore.isPending.value" @click="pendingRestore = null"
            >Cancel</Button
          >
          <Button :disabled="restore.isPending.value" @click="confirmRestore">
            <Loader2
              v-if="restore.isPending.value"
              class="size-3.5 animate-spin"
              aria-hidden="true"
            />
            Restore
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog v-model:open="permissionsOpen">
      <DialogContent class="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Page permissions</DialogTitle>
          <DialogDescription>
            Restrict who may read or write this page. Everyone with project access can see it until
            a rule says otherwise.
          </DialogDescription>
        </DialogHeader>

        <div class="space-y-3">
          <p v-if="permissionsQuery.isLoading.value" class="text-muted-foreground text-sm">
            Loading…
          </p>

          <ul v-else class="divide-border divide-y">
            <li
              v-for="rule in rules"
              :key="`${rule.subjectKind}:${rule.subjectId}`"
              class="flex items-center justify-between gap-2 py-2 text-sm"
            >
              <span class="min-w-0 flex-1 truncate">{{ subjectLabel(rule) }}</span>
              <select
                :value="rule.access"
                class="border-border bg-background h-7 rounded-lg border px-2 text-xs capitalize"
                @change="
                  setRuleAccess(
                    rule,
                    ($event.target as HTMLSelectElement).value as WikiPermission['access'],
                  )
                "
              >
                <option value="read">read</option>
                <option value="write">write</option>
              </select>
              <button
                class="text-muted-foreground flex-none hover:text-destructive"
                :aria-label="`Remove ${subjectLabel(rule)}`"
                @click="removeRule(rule)"
              >
                <Trash2 class="size-3.5" aria-hidden="true" />
              </button>
            </li>
            <li v-if="rules.length === 0" class="text-muted-foreground py-2 text-sm">
              No rules — visible to everyone with project access.
            </li>
          </ul>

          <div class="border-border flex items-end gap-2 border-t pt-3">
            <select
              :value="newKind"
              class="border-border bg-background h-8 rounded-lg border px-2 text-xs"
              @change="
                newKind = ($event.target as HTMLSelectElement)
                  .value as WikiPermission['subjectKind']
              "
            >
              <option value="user">Person</option>
              <option value="team">Team</option>
              <option value="projectRole">Project role</option>
            </select>

            <UserSelect
              v-if="newKind === 'user'"
              class="flex-1"
              :model-value="newUserId"
              :options="userOptions"
              :loading="membersQuery.isLoading.value"
              label="Pick a person"
              placeholder="Choose a person…"
              @update:model-value="(value) => (newUserId = value as string | null)"
            />

            <select
              v-else-if="newKind === 'team'"
              :value="newTeamId"
              class="border-border bg-background h-8 flex-1 rounded-lg border px-2 text-xs"
              @change="newTeamId = ($event.target as HTMLSelectElement).value"
            >
              <option value="" disabled>Choose a team…</option>
              <option v-for="team in teamsQuery.data.value ?? []" :key="team.id" :value="team.id">
                {{ team.name }}
              </option>
            </select>

            <select
              v-else
              :value="newRole"
              class="border-border bg-background h-8 flex-1 rounded-lg border px-2 text-xs capitalize"
              @change="newRole = ($event.target as HTMLSelectElement).value as ProjectRole"
            >
              <option v-for="role in projectRoles" :key="role" :value="role">{{ role }}</option>
            </select>

            <select
              :value="newAccess"
              class="border-border bg-background h-8 rounded-lg border px-2 text-xs capitalize"
              @change="
                newAccess = ($event.target as HTMLSelectElement).value as WikiPermission['access']
              "
            >
              <option value="read">read</option>
              <option value="write">write</option>
            </select>

            <Button size="sm" variant="outline" :disabled="!canAddRule" @click="addRule">
              <Plus class="size-3.5" aria-hidden="true" />
              Add
            </Button>
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" :disabled="savingPermissions" @click="permissionsOpen = false"
            >Cancel</Button
          >
          <Button :disabled="savingPermissions" @click="savePermissions">
            <Loader2 v-if="savingPermissions" class="size-3.5 animate-spin" aria-hidden="true" />
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </AppShell>
</template>
