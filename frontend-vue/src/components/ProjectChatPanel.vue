<script setup lang="ts">
import {
  FileArchive,
  FileText,
  Image as ImageIcon,
  Loader2,
  MessageCircle,
  Paperclip,
  Send,
  Trash2,
  TriangleAlert,
  X,
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'

import { attachmentUrl, deleteAttachment, uploadAttachment } from '@/api/attachments'
import {
  askProjectChat,
  chatAttachmentAccept,
  clearProjectChat,
  getProjectChat,
  MAX_CHAT_ATTACHMENTS,
  type ProjectChatMessage,
  type ProjectChatView,
} from '@/api/project-chat'
import Markdown from '@/components/common/Markdown.vue'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { useOrganizationsStore } from '@/stores/organizations'
import { ApiError } from '@/utils/api'

/**
 * Ask the project's default agent about the project without filing a ticket: a round
 * button in the lower-right corner opens a small chat over the page. Each question is a
 * project chat run, so it needs a runner like any other run; while one is in progress the
 * panel polls for its answer and says plainly when no runner is there to take it.
 *
 * Files - images, PDFs, text and ZIPs - upload as soon as they are picked, so a file the
 * agent could not read is refused with its reason before the question goes. They stay
 * selected until the question is accepted; the agent reads them on the runner.
 *
 * Only for those who may operate the factory, like every other way of starting a run.
 */
const props = defineProps<{ slug: string; projectKey: string }>()

const MAX_QUESTION = 4000
const POLL_MS = 2500
/** Mirrors the server's default `Attachments:MaxBytes`; the server decides. */
const MAX_FILE_BYTES = 25 * 1024 * 1024

/** A picked file: uploading, uploaded as a pending attachment, or refused with the reason. */
interface Selection {
  key: number
  file: File
  status: 'uploading' | 'ready' | 'failed'
  attachmentId: string | null
  error: string | null
}

const organizations = useOrganizationsStore()
const canOperate = computed(() => organizations.current?.canOperateFactory === true)

const open = ref(false)
const view = ref<ProjectChatView | null>(null)
const loading = ref(false)
const question = ref('')
const sending = ref(false)
const clearing = ref(false)
const error = ref<string | null>(null)
const list = ref<HTMLElement | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)
const selections = ref<Selection[]>([])
let nextKey = 0

const messages = computed(() => view.value?.messages ?? [])
const pending = computed(() => messages.value.find((m) => m.status === 'pending') ?? null)
const uploading = computed(() => selections.value.some((s) => s.status === 'uploading'))
const refused = computed(() => selections.value.some((s) => s.status === 'failed'))
const canSend = computed(
  () =>
    !sending.value &&
    !clearing.value &&
    pending.value === null &&
    !uploading.value &&
    !refused.value &&
    question.value.trim().length > 0 &&
    question.value.length <= MAX_QUESTION,
)

/** The problem's own words: the field message when there is one, then its detail or title. */
function describe(failure: unknown): string {
  if (failure instanceof ApiError) {
    const fields = failure.fieldErrors
    return (
      fields.question?.[0] ??
      fields.attachmentIds?.[0] ??
      fields.file?.[0] ??
      fields.contentType?.[0] ??
      fields.sizeBytes?.[0] ??
      fields.fileName?.[0] ??
      fields.agentId?.[0] ??
      fields.runnerId?.[0] ??
      (typeof failure.problem?.detail === 'string' ? failure.problem.detail : null) ??
      failure.title
    )
  }
  return 'Something went wrong. Try again.'
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function fileIcon(contentType: string, fileName: string) {
  if (contentType.startsWith('image/')) return ImageIcon
  if (contentType === 'application/zip' || /\.zip$/i.test(fileName)) return FileArchive
  return FileText
}

function pickFiles() {
  fileInput.value?.click()
}

function onFilesPicked(event: Event) {
  const input = event.target as HTMLInputElement
  addFiles([...(input.files ?? [])])
  // The same file picked again after removing it must fire `change` again.
  input.value = ''
}

function addFiles(files: File[]) {
  error.value = null
  const room = MAX_CHAT_ATTACHMENTS - selections.value.length
  if (files.length > room) {
    error.value = `Attach at most ${MAX_CHAT_ATTACHMENTS} files to one question.`
  }
  for (const file of files.slice(0, Math.max(0, room))) {
    const selection: Selection = {
      key: nextKey++,
      file,
      status: 'uploading',
      attachmentId: null,
      error: null,
    }
    selections.value.push(selection)
    if (file.size === 0 || file.size > MAX_FILE_BYTES) {
      fail(selection.key, `${file.name} must be between 1 byte and 25 MB.`)
      continue
    }
    void upload(selection.key, file)
  }
}

function find(key: number): Selection | undefined {
  return selections.value.find((s) => s.key === key)
}

function fail(key: number, message: string) {
  const selection = find(key)
  if (!selection) return
  selection.status = 'failed'
  selection.error = message
}

async function upload(key: number, file: File) {
  const mine = generation
  try {
    const id = await uploadAttachment(props.slug, props.projectKey, file, 'chat')
    const selection = mine === generation ? find(key) : undefined
    // Removed while it uploaded: nothing will send it, so it need not wait for the sweeper.
    if (!selection) {
      void deleteAttachment(props.slug, id).catch(() => undefined)
      return
    }
    selection.status = 'ready'
    selection.attachmentId = id
  } catch (failure) {
    if (mine === generation) fail(key, `${file.name}: ${describe(failure)}`)
  }
}

function removeSelection(key: number) {
  const selection = find(key)
  selections.value = selections.value.filter((s) => s.key !== key)
  if (selection?.attachmentId) {
    void deleteAttachment(props.slug, selection.attachmentId).catch(() => undefined)
  }
}

function runningLabel(message: ProjectChatMessage): string {
  if (message.runStatus === 'running') return 'Running'
  if (message.runStatus === 'assigned') return 'Starting'
  return 'Queued'
}

const failedLabel: Record<string, string> = {
  failed: 'Failed',
  cancelled: 'Cancelled',
  timedOut: 'Timed out',
}

async function scrollToEnd() {
  await nextTick()
  if (list.value) list.value.scrollTop = list.value.scrollHeight
}

// A poll answer for the project the panel showed a moment ago is dropped, not shown.
let generation = 0
let timer: ReturnType<typeof setTimeout> | undefined

function stopPolling() {
  clearTimeout(timer)
  timer = undefined
}

function schedulePoll() {
  stopPolling()
  if (!pending.value) return
  timer = setTimeout(() => void refresh(), POLL_MS)
}

function show(next: ProjectChatView) {
  const before = view.value?.messages.map((m) => `${m.id}:${m.status}`).join() ?? ''
  view.value = next
  if (next.messages.map((m) => `${m.id}:${m.status}`).join() !== before) void scrollToEnd()
  schedulePoll()
}

async function refresh() {
  const mine = generation
  try {
    const next = await getProjectChat(props.slug, props.projectKey)
    if (mine === generation) show(next)
  } catch (failure) {
    if (mine !== generation) return
    error.value = describe(failure)
    // A blip should not end the wait for an answer; try again on the next beat.
    schedulePoll()
  }
}

async function load() {
  loading.value = true
  try {
    await refresh()
  } finally {
    loading.value = false
  }
}

async function send() {
  if (!canSend.value) return
  const mine = generation
  sending.value = true
  error.value = null
  try {
    const next = await askProjectChat(
      props.slug,
      props.projectKey,
      question.value.trim(),
      selections.value.map((s) => s.attachmentId!),
    )
    if (mine !== generation) return
    question.value = ''
    // Sent: the files belong to the question now, so they are no longer the composer's.
    selections.value = []
    show(next)
  } catch (failure) {
    if (mine !== generation) return
    error.value = describe(failure)
    // Most likely a question asked from another tab: show it and wait for its answer.
    if (failure instanceof ApiError && failure.status === 409) void refresh()
  } finally {
    sending.value = false
  }
}

async function clear() {
  if (clearing.value) return
  const mine = generation
  clearing.value = true
  error.value = null
  try {
    await clearProjectChat(props.slug, props.projectKey)
    if (mine !== generation) return
    stopPolling()
    view.value = { messages: [], runnerOnline: view.value?.runnerOnline ?? true }
  } catch (failure) {
    if (mine === generation) error.value = describe(failure)
  } finally {
    clearing.value = false
  }
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  void send()
}

function toggle() {
  open.value = !open.value
}

watch(open, (isOpen) => {
  if (isOpen) void load()
})

// Another project is another conversation: forget this one and read that one.
watch(
  () => [props.slug, props.projectKey],
  () => {
    generation++
    stopPolling()
    view.value = null
    error.value = null
    question.value = ''
    selections.value = []
    if (open.value) void load()
  },
)

// Losing the right to operate the factory (switching organization) closes the panel.
watch(canOperate, (may) => {
  if (!may) {
    open.value = false
    stopPolling()
  }
})

onBeforeUnmount(() => {
  generation++
  stopPolling()
})
</script>

<template>
  <template v-if="canOperate">
    <button
      v-if="!open"
      type="button"
      class="bg-primary text-primary-foreground hover:bg-primary/90 focus-visible:ring-ring/50 fixed right-5 bottom-5 z-40 flex size-12 items-center justify-center rounded-full shadow-lg outline-none focus-visible:ring-3"
      aria-label="Ask the agent about this project"
      title="Ask the agent about this project"
      data-testid="project-chat-button"
      @click="toggle"
    >
      <MessageCircle class="size-5" aria-hidden="true" />
    </button>

    <section
      v-else
      class="bg-popover text-popover-foreground border-border fixed right-5 bottom-5 z-40 flex max-h-[min(640px,calc(100dvh-2.5rem))] w-[min(420px,calc(100vw-2.5rem))] flex-col rounded-xl border shadow-2xl"
      role="dialog"
      aria-label="Project chat"
      data-testid="project-chat-panel"
      @keydown.esc="open = false"
    >
      <header class="border-border flex items-center gap-2 border-b px-4 py-2.5">
        <MessageCircle class="text-muted-foreground size-4" aria-hidden="true" />
        <h2 class="min-w-0 flex-1 truncate text-sm font-medium">Ask about {{ projectKey }}</h2>
        <Button
          variant="ghost"
          size="sm"
          :disabled="clearing || messages.length === 0"
          title="Clear history"
          data-testid="project-chat-clear"
          @click="clear"
        >
          <Trash2 class="size-3.5" aria-hidden="true" />
          Clear history
        </Button>
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label="Close project chat"
          title="Close"
          @click="open = false"
        >
          <X class="size-4" aria-hidden="true" />
        </Button>
      </header>

      <div ref="list" class="min-h-32 flex-1 space-y-3 overflow-y-auto px-4 py-3">
        <p v-if="loading && !view" class="text-muted-foreground flex items-center gap-2 text-sm">
          <Loader2 class="size-4 animate-spin" aria-hidden="true" />
          Loading
        </p>
        <p v-else-if="messages.length === 0" class="text-muted-foreground text-sm">
          Ask the agent about this project: how something works, what is left to do, or to file
          tickets for you. It reads the project's code, wiki and items to answer.
        </p>

        <div
          v-for="message in messages"
          :key="message.id"
          class="space-y-2"
          data-testid="project-chat-message"
          :data-status="message.status"
        >
          <div class="flex justify-end">
            <p
              class="bg-primary text-primary-foreground max-w-[85%] rounded-lg rounded-br-sm px-3 py-2 text-sm break-words whitespace-pre-wrap"
            >
              {{ message.question }}
            </p>
          </div>
          <ul
            v-if="message.attachments?.length"
            class="flex flex-wrap justify-end gap-1.5"
            aria-label="Files sent with the question"
            data-testid="project-chat-message-files"
          >
            <li v-for="file in message.attachments" :key="file.id">
              <a
                :href="attachmentUrl(slug, file.id)"
                target="_blank"
                rel="noopener"
                class="border-border bg-background hover:bg-muted flex max-w-56 items-center gap-1.5 rounded-md border px-2 py-1 text-xs"
                :title="`${file.fileName} (${formatSize(file.sizeBytes)})`"
                data-testid="project-chat-message-file"
              >
                <img
                  v-if="file.contentType.startsWith('image/')"
                  :src="attachmentUrl(slug, file.id)"
                  alt=""
                  class="size-6 shrink-0 rounded object-cover"
                />
                <component
                  :is="fileIcon(file.contentType, file.fileName)"
                  v-else
                  class="text-muted-foreground size-3.5 shrink-0"
                  aria-hidden="true"
                />
                <span class="truncate">{{ file.fileName }}</span>
              </a>
            </li>
          </ul>

          <div
            v-if="message.status === 'pending'"
            class="space-y-1"
            data-testid="project-chat-running"
          >
            <p class="text-muted-foreground flex items-center gap-2 text-sm">
              <Loader2 class="size-4 animate-spin" aria-hidden="true" />
              {{ runningLabel(message) }}
            </p>
            <p
              v-if="message.runStatus === 'queued' && view?.runnerOnline === false"
              class="text-xs text-amber-700 dark:text-amber-400"
              data-testid="project-chat-no-runner"
            >
              <template v-if="view.runnerName">
                {{ view.runnerName }}, this project's default runner, is offline. Your question will
                start when it connects.
              </template>
              <template v-else>
                No runner is online. Your question will start when a runner connects.
              </template>
            </p>
          </div>

          <div
            v-else-if="message.status === 'answered'"
            class="bg-muted max-w-full rounded-lg rounded-bl-sm px-3 py-2"
          >
            <Markdown :source="message.answer ?? ''" :slug="slug" :project-key="projectKey" />
          </div>

          <p
            v-else
            class="text-destructive border-destructive/40 bg-destructive/5 flex items-start gap-2 rounded-lg border px-3 py-2 text-sm"
            data-testid="project-chat-message-error"
          >
            <TriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
            <span>
              <strong class="font-medium">{{ failedLabel[message.status] ?? 'Failed' }}.</strong>
              {{ message.error ?? 'The agent did not answer.' }}
            </span>
          </p>
        </div>
      </div>

      <form class="border-border space-y-2 border-t px-4 py-3" @submit.prevent="send">
        <p
          v-if="error"
          class="text-destructive text-xs"
          role="alert"
          data-testid="project-chat-error"
        >
          {{ error }}
        </p>
        <ul
          v-if="selections.length > 0"
          class="space-y-1"
          aria-label="Files to send"
          data-testid="project-chat-selected-files"
        >
          <li
            v-for="selection in selections"
            :key="selection.key"
            class="border-border flex items-start gap-2 rounded-md border px-2 py-1.5 text-xs"
            :class="selection.status === 'failed' ? 'border-destructive/40 bg-destructive/5' : ''"
            data-testid="project-chat-selected-file"
            :data-status="selection.status"
          >
            <Loader2
              v-if="selection.status === 'uploading'"
              class="text-muted-foreground mt-px size-3.5 shrink-0 animate-spin"
              aria-hidden="true"
            />
            <TriangleAlert
              v-else-if="selection.status === 'failed'"
              class="text-destructive mt-px size-3.5 shrink-0"
              aria-hidden="true"
            />
            <component
              :is="fileIcon(selection.file.type, selection.file.name)"
              v-else
              class="text-muted-foreground mt-px size-3.5 shrink-0"
              aria-hidden="true"
            />
            <span class="min-w-0 flex-1">
              <span class="flex gap-1.5">
                <span class="truncate font-medium">{{ selection.file.name }}</span>
                <span class="text-muted-foreground shrink-0">{{
                  formatSize(selection.file.size)
                }}</span>
              </span>
              <span
                v-if="selection.error"
                class="text-destructive block"
                data-testid="project-chat-file-error"
              >
                {{ selection.error }}
              </span>
            </span>
            <button
              type="button"
              class="text-muted-foreground hover:text-foreground focus-visible:ring-ring/50 -m-0.5 rounded p-0.5 outline-none focus-visible:ring-2"
              :aria-label="`Remove ${selection.file.name}`"
              :title="`Remove ${selection.file.name}`"
              data-testid="project-chat-remove-file"
              @click="removeSelection(selection.key)"
            >
              <X class="size-3.5" aria-hidden="true" />
            </button>
          </li>
        </ul>
        <Textarea
          v-model="question"
          class="max-h-40 min-h-10 resize-none"
          placeholder="Ask a question. Enter sends, Shift+Enter adds a line."
          aria-label="Your question"
          :maxlength="MAX_QUESTION"
          data-testid="project-chat-input"
          @keydown="onKeydown"
        />
        <div class="flex items-center justify-end gap-2">
          <input
            ref="fileInput"
            type="file"
            multiple
            class="hidden"
            :accept="chatAttachmentAccept"
            data-testid="project-chat-file-input"
            @change="onFilesPicked"
          />
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            :disabled="selections.length >= MAX_CHAT_ATTACHMENTS || sending"
            aria-label="Attach files"
            :title="`Attach images, PDFs, text or ZIP files (up to ${MAX_CHAT_ATTACHMENTS})`"
            data-testid="project-chat-attach"
            @click="pickFiles"
          >
            <Paperclip class="size-4" aria-hidden="true" />
          </Button>
          <span v-if="pending" class="text-muted-foreground mr-auto text-xs">
            Wait for the answer before asking again.
          </span>
          <span v-else-if="refused" class="text-destructive mr-auto text-xs">
            Remove the files that cannot be sent.
          </span>
          <span v-else-if="uploading" class="text-muted-foreground mr-auto text-xs">
            Uploading files...
          </span>
          <span v-else class="mr-auto" />
          <Button type="submit" size="sm" :disabled="!canSend" data-testid="project-chat-send">
            <Send class="size-3.5" aria-hidden="true" />
            Send
          </Button>
        </div>
      </form>
    </section>
  </template>
</template>
