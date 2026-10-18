<script setup lang="ts">
import { Loader2, MessageCircle, Send, Trash2, TriangleAlert, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'

import {
  askProjectChat,
  clearProjectChat,
  getProjectChat,
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
 * Only for those who may operate the factory, like every other way of starting a run.
 */
const props = defineProps<{ slug: string; projectKey: string }>()

const MAX_QUESTION = 4000
const POLL_MS = 2500

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

const messages = computed(() => view.value?.messages ?? [])
const pending = computed(() => messages.value.find((m) => m.status === 'pending') ?? null)
const canSend = computed(
  () =>
    !sending.value &&
    !clearing.value &&
    pending.value === null &&
    question.value.trim().length > 0 &&
    question.value.length <= MAX_QUESTION,
)

/** The problem's own words: the field message when there is one, then its detail or title. */
function describe(failure: unknown): string {
  if (failure instanceof ApiError) {
    const fields = failure.fieldErrors
    return (
      fields.question?.[0] ??
      fields.agentId?.[0] ??
      (typeof failure.problem?.detail === 'string' ? failure.problem.detail : null) ??
      failure.title
    )
  }
  return 'Something went wrong. Try again.'
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
    const next = await askProjectChat(props.slug, props.projectKey, question.value.trim())
    if (mine !== generation) return
    question.value = ''
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
          Ask the agent about this project: how something works, what is left to do, or to
          file tickets for you. It reads the project's code, wiki and items to answer.
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

          <div v-if="message.status === 'pending'" class="space-y-1" data-testid="project-chat-running">
            <p class="text-muted-foreground flex items-center gap-2 text-sm">
              <Loader2 class="size-4 animate-spin" aria-hidden="true" />
              {{ runningLabel(message) }}
            </p>
            <p
              v-if="message.runStatus === 'queued' && view?.runnerOnline === false"
              class="text-xs text-amber-700 dark:text-amber-400"
              data-testid="project-chat-no-runner"
            >
              No runner is online. Your question will start when a runner connects.
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
        <p v-if="error" class="text-destructive text-xs" role="alert" data-testid="project-chat-error">
          {{ error }}
        </p>
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
          <span v-if="pending" class="text-muted-foreground mr-auto text-xs">
            Wait for the answer before asking again.
          </span>
          <Button type="submit" size="sm" :disabled="!canSend" data-testid="project-chat-send">
            <Send class="size-3.5" aria-hidden="true" />
            Send
          </Button>
        </div>
      </form>
    </section>
  </template>
</template>
