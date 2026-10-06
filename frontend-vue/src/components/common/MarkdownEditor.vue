<script setup lang="ts">
import Image from '@tiptap/extension-image'
import { Table } from '@tiptap/extension-table'
import TableCell from '@tiptap/extension-table-cell'
import TableHeader from '@tiptap/extension-table-header'
import TableRow from '@tiptap/extension-table-row'
import TaskItem from '@tiptap/extension-task-item'
import TaskList from '@tiptap/extension-task-list'
import { Markdown } from '@tiptap/markdown'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import StarterKit from '@tiptap/starter-kit'
import { EditorContent, Extension, useEditor } from '@tiptap/vue-3'
import { Bold, Code, Hash, List, ListTodo, Minus, Paperclip, Plus, Table as TableIcon, Trash2 } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import { searchProject, type SearchItem } from '@/api/search'
import UserAvatar from '@/components/common/UserAvatar.vue'
import { useResolvedItemKeys } from '@/composables/useResolvedItemKeys'
import { useToast } from '@/composables/useToast'
import { mentionToken, type Mentionable, searchMentionables } from '@/lib/mentions'
import { planLimitMessage } from '@/lib/billing'
import { itemHref, itemReferencePattern } from '@/lib/markdown'
import { ApiError } from '@/utils/api'

/**
 * `upload` turns a file into a same-origin URL (see `api/attachments.ts`). Without it the
 * editor accepts no files - pasting an image stays the browser's business, which for
 * Markdown means nothing is inserted.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string
    placeholder?: string
    disabled?: boolean
    compact?: boolean
    upload?: (file: File) => Promise<string>
    accept?: string
    /** People an "@" can tag. Without it, typing "@" is just typing. */
    mentionables?: Mentionable[]
    /** Put the cursor at the end as soon as the editor exists, e.g. in a reply box just opened. */
    autofocus?: boolean
    /** Both are required to search tickets from this project. */
    slug?: string
    projectKey?: string
    /** Show `#KEY` references to existing tickets as links that open them. Needs slug and projectKey. */
    linkTickets?: boolean
  }>(),
  {
    placeholder: 'Write a description…',
    disabled: false,
    compact: false,
    upload: undefined,
    accept: undefined,
    mentionables: undefined,
    autofocus: false,
    slug: undefined,
    projectKey: undefined,
    linkTickets: false,
  },
)
const emit = defineEmits<{ 'update:modelValue': [markdown: string]; blur: [] }>()
const toast = useToast()
const uploading = ref(0)
const fileInput = ref<HTMLInputElement | null>(null)
defineExpose({
  uploading: computed(() => uploading.value > 0),
  focus: () => editor.value?.commands.focus('end'),
})

// ── Mentions ───────────────────────────────────────────────────────────────────────
// "@" followed by the start of a name opens a list of the people who can see this item.
// Picking one writes "@AnaKovač" as plain text: the Markdown stays readable anywhere, and
// the server resolves the same token back to the person when the comment is saved.
const root = ref<HTMLElement | null>(null)
const mention = ref<{ from: number; to: number; query: string; left: number; top: number } | null>(null)
const mentionIndex = ref(0)
const mentionMatches = computed(() =>
  mention.value && props.mentionables ? searchMentionables(props.mentionables, mention.value.query) : [],
)
watch(mentionMatches, () => (mentionIndex.value = 0))

function trackMention() {
  const current = editor.value
  if (!current || !props.mentionables?.length || !current.isFocused) return void (mention.value = null)
  const { $from, empty } = current.state.selection
  if (!empty || $from.parent.type.spec.code || current.isActive('code')) return void (mention.value = null)
  const before = $from.parent.textBetween(0, $from.parentOffset, undefined, '\ufffc')
  const match = /(?:^|[\s(])@([\p{L}\p{N}-]{0,40})$/u.exec(before)
  if (!match) return void (mention.value = null)
  const query = match[1]!
  const from = $from.pos - query.length - 1
  const at = current.view.coordsAtPos(from)
  const box = root.value?.getBoundingClientRect()
  mention.value = {
    from,
    to: $from.pos,
    query,
    left: box ? at.left - box.left : 0,
    top: box ? at.bottom - box.top + 4 : 0,
  }
}

function pickMention(person: Mentionable) {
  const range = mention.value
  if (!range) return
  mention.value = null
  editor.value
    ?.chain()
    .focus()
    .insertContentAt({ from: range.from, to: range.to }, `@${mentionToken(person.name)} `)
    .run()
}

/** Arrow keys, Enter and Tab drive the open list; true means the editor must not also act. */
function mentionKey(event: KeyboardEvent): boolean {
  const matches = mentionMatches.value
  if (!mention.value || matches.length === 0) return false
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    const step = event.key === 'ArrowDown' ? 1 : -1
    mentionIndex.value = (mentionIndex.value + step + matches.length) % matches.length
    return true
  }
  if (event.key === 'Enter' || event.key === 'Tab') {
    pickMention(matches[mentionIndex.value]!)
    return true
  }
  return false
}

// Ticket suggestions are fetched only while a # trigger is active. Watch cleanup prevents
// old responses from replacing a newer query, even after a project change or Escape.
const ticket = ref<{ from: number; to: number; query: string; left: number; top: number } | null>(null)
const ticketIndex = ref(0)
const ticketMatches = ref<SearchItem[]>([])
const ticketLoading = ref(false)
const ticketError = ref(false)
watch([() => ticket.value?.query, () => props.slug, () => props.projectKey], (_value, _old, cleanup) => {
  ticketMatches.value = []
  ticketIndex.value = 0
  ticketError.value = false
  ticketLoading.value = !!ticket.value
  if (!ticket.value || !props.slug || !props.projectKey) return
  const { query } = ticket.value
  const slug = props.slug
  const projectKey = props.projectKey
  let active = true
  const timer = setTimeout(async () => {
    try {
      const results = await searchProject(slug, projectKey, query, { types: 'items', limit: 8 })
      if (active) ticketMatches.value = results.items.filter((item) => item.key.startsWith(`${projectKey}-`)).slice(0, 8)
    } catch {
      if (active) ticketError.value = true
    } finally {
      if (active) ticketLoading.value = false
    }
  }, 150)
  cleanup(() => { active = false; clearTimeout(timer) })
})

function trackTicket() {
  const current = editor.value
  if (!current || !props.slug || !props.projectKey || props.disabled || !current.isFocused)
    return void (ticket.value = null)
  const { $from, empty } = current.state.selection
  if (!empty || $from.parent.type.spec.code || current.isActive('code') || current.isActive('link'))
    return void (ticket.value = null)
  const before = $from.parent.textBetween(0, $from.parentOffset, undefined, '\ufffc')
  const match = /(?:^|[\s(])#([\p{L}\p{N}-]+(?: [\p{L}\p{N}-]+)*)?$/u.exec(before)
  if (!match) return void (ticket.value = null)
  const query = match[1] ?? ''
  if (query.length > 80) return void (ticket.value = null)
  const from = $from.pos - query.length - 1
  const at = current.view.coordsAtPos(from)
  const box = root.value?.getBoundingClientRect()
  ticket.value = { from, to: $from.pos, query, left: box ? at.left - box.left : 0, top: box ? at.bottom - box.top + 4 : 0 }
}

function trackSuggestions() {
  trackMention()
  trackTicket()
}

function pickTicket(item: SearchItem) {
  const range = ticket.value
  if (!range) return
  ticket.value = null
  editor.value?.chain().focus().insertContentAt({ from: range.from, to: range.to }, { type: 'text', text: `#${item.key} ` }).run()
}

function ticketKey(event: KeyboardEvent): boolean {
  if (!ticket.value) return false
  const matches = ticketMatches.value
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    if (matches.length) ticketIndex.value = (ticketIndex.value + (event.key === 'ArrowDown' ? 1 : -1) + matches.length) % matches.length
    return true
  }
  if ((event.key === 'Enter' || event.key === 'Tab') && matches.length) {
    pickTicket(matches[ticketIndex.value]!)
    return true
  }
  return false
}

function insertTicketTrigger() {
  const current = editor.value
  if (!current) return
  const { $from } = current.state.selection
  const before = $from.parent.textBetween(0, $from.parentOffset)
  current.chain().focus().insertContent({ type: 'text', text: (!before || /[\s(]$/.test(before) ? '' : ' ') + '#' }).run()
  trackTicket()
}

// ── Ticket links ───────────────────────────────────────────────────────────────────
// `#KEY` stays plain text in the document and the Markdown; links are decorations drawn
// over it, so they never reach the saved description. Only keys the API resolved are
// drawn, like the rendered Markdown does. Set once per editor, so the lookups (and the
// query client and router they need) exist only where links were asked for.
const router = props.linkTickets ? useRouter() : undefined
const linkedKeys = props.linkTickets
  ? useResolvedItemKeys(() => props.modelValue, () => props.slug, () => props.projectKey)
  : computed<string[]>(() => [])
const ticketLinkKey = new PluginKey<DecorationSet>('ticketLinks')

function ticketLinkDecorations(doc: ProseMirrorNode): DecorationSet {
  const { slug, projectKey } = props
  const keys = new Set(linkedKeys.value)
  if (!slug || !projectKey || !keys.size) return DecorationSet.empty
  const links: Decoration[] = []
  doc.descendants((node, pos, parent) => {
    if (!node.isText || parent?.type.spec.code) return
    if (node.marks.some((mark) => mark.type.name === 'code' || mark.type.name === 'link')) return
    for (const match of node.text!.matchAll(itemReferencePattern)) {
      const key = match[2]!
      if (!key.startsWith(`${projectKey}-`) || !keys.has(key)) continue
      const from = pos + match.index! + match[1]!.length
      links.push(Decoration.inline(from, from + key.length + 1, {
        nodeName: 'a',
        href: itemHref(slug, projectKey, key),
        class: 'cursor-pointer',
        'data-ticket-link': key,
      }))
    }
  })
  return DecorationSet.create(doc, links)
}

const TicketLinks = Extension.create({
  name: 'ticketLinks',
  addProseMirrorPlugins: () => [
    new Plugin({
      key: ticketLinkKey,
      state: {
        init: (_config, state) => ticketLinkDecorations(state.doc),
        apply: (tr, links) => (tr.docChanged || tr.getMeta(ticketLinkKey) ? ticketLinkDecorations(tr.doc) : links),
      },
      props: { decorations: (state) => ticketLinkKey.getState(state) },
    }),
  ],
})

/** A plain click opens the ticket in the app; with a modifier it goes to a new tab. */
function openTicketLink(event: MouseEvent): boolean {
  if (!router || event.button !== 0) return false
  const href = (event.target as Element).closest?.('a[data-ticket-link]')?.getAttribute('href')
  if (!href) return false
  event.preventDefault()
  if (event.ctrlKey || event.metaKey || event.shiftKey) window.open(href, '_blank', 'noopener')
  else void router.push(href)
  return true
}

const editor = useEditor({
  content: props.modelValue,
  contentType: 'markdown',
  editable: !props.disabled,
  autofocus: props.autofocus ? 'end' : false,
  extensions: [StarterKit, TaskList, TaskItem.configure({ nested: true }), Image, Table.configure({ resizable: true }), TableRow, TableHeader, TableCell, Markdown, TicketLinks],
  editorProps: {
    attributes: {
      class: `aictiq-markdown ${props.compact ? 'min-h-20' : 'min-h-44'} px-3 py-2 text-sm outline-none`,
      'data-placeholder': props.placeholder,
    },
    // ProseMirror binds Escape to "select parent node" and marks the key handled, which a
    // surrounding dialog reads as "do not close". Escape in a text box means "leave", so
    // the editor steps aside: true here skips ProseMirror without preventing the default.
    // With the mention list open, Escape closes the list and nothing else.
    handleDOMEvents: {
      keydown: (_view, event) => {
        if (event.key !== 'Escape') return false
        if (mention.value || ticket.value) {
          mention.value = null
          ticket.value = null
          event.preventDefault()
          event.stopPropagation()
        }
        return true
      },
    },
    handleKeyDown: (_view, event) => ticketKey(event) || mentionKey(event),
    // Ahead of the Link extension's own handler, which would open the href in a new window.
    handleClick: (_view, _pos, event) => openTicketLink(event),
    handlePaste: (_view, event) => insertFiles(event.clipboardData?.files),
    handleDrop: (view, event) => {
      const at = view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos
      return insertFiles(event.dataTransfer?.files, at)
    },
  },
  onUpdate: ({ editor: current }) => emit('update:modelValue', current.getMarkdown()),
  onSelectionUpdate: trackSuggestions,
  onTransaction: trackSuggestions,
  // Leaving the editor to pick a file is not "done editing" - a blur-save would race the upload.
  onBlur: () => {
    mention.value = null
    ticket.value = null
    if (uploading.value === 0) emit('blur')
  },
})

/** True when the event carried files and the editor took ownership of it. */
function insertFiles(files: FileList | null | undefined, at?: number): boolean {
  if (!props.upload || !files?.length || props.disabled) return false
  for (const file of Array.from(files)) void insertFile(file, at)
  return true
}

async function insertFile(file: File, at?: number) {
  const current = editor.value
  if (!current || !props.upload) return
  uploading.value++
  try {
    const src = await props.upload(file)
    const position = at ?? current.state.selection.to
    const content = file.type.startsWith('image/')
      ? { type: 'image', attrs: { src, alt: file.name } }
      : { type: 'text', text: file.name, marks: [{ type: 'link', attrs: { href: src } }] }
    current.chain().focus().insertContentAt(Math.min(position, current.state.doc.content.size), content).run()
  } catch (error) {
    // A 402 is the organization's attachment allowance, and there is no larger one to sell:
    // say what to delete rather than letting a generic "Plan limit reached" stand.
    const quota = planLimitMessage(error)
    const field = error instanceof ApiError ? Object.values(error.fieldErrors).flat()[0] : undefined
    if (quota) toast.error(undefined, `${file.name}: ${quota}`)
    else if (field) toast.error(undefined, `${file.name}: ${field}`)
    else toast.error(error, error instanceof Error ? error.message : `${file.name} could not be uploaded.`)
  } finally {
    uploading.value--
    if (uploading.value === 0 && !current.isFocused) emit('blur')
  }
}

const actions = [
  { label: 'Bold', icon: Bold, run: () => editor.value?.chain().focus().toggleBold().run() },
  { label: 'Bullet list', icon: List, run: () => editor.value?.chain().focus().toggleBulletList().run() },
  { label: 'Task list', icon: ListTodo, run: () => editor.value?.chain().focus().toggleTaskList().run() },
  { label: 'Code block', icon: Code, run: () => editor.value?.chain().focus().toggleCodeBlock().run() },
  {
    label: 'Insert table',
    icon: TableIcon,
    run: () => editor.value?.chain().focus().insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run(),
  },
]

// Shown only while the cursor is in a table. Markdown tables have one header row and no
// merged cells, so merge/split and header toggles are deliberately absent. Words rather than
// icons: the row/column insert glyphs are too easy to read the wrong way round.
const tableActions = [
  { label: 'Add row below', text: 'Row', icon: Plus, run: () => editor.value?.chain().focus().addRowAfter().run() },
  { label: 'Add column right', text: 'Column', icon: Plus, run: () => editor.value?.chain().focus().addColumnAfter().run() },
  { label: 'Delete row', text: 'Row', icon: Minus, run: () => editor.value?.chain().focus().deleteRow().run() },
  { label: 'Delete column', text: 'Column', icon: Minus, run: () => editor.value?.chain().focus().deleteColumn().run() },
  { label: 'Delete table', text: '', icon: Trash2, run: () => editor.value?.chain().focus().deleteTable().run() },
]
const inTable = computed(() => !!editor.value?.isActive('table'))

function pickFiles(event: Event) {
  const input = event.target as HTMLInputElement
  insertFiles(input.files)
  input.value = ''
}

watch(() => props.modelValue, (markdown) => {
  if (!editor.value || editor.value.getMarkdown() === markdown) return
  editor.value.commands.setContent(markdown, { contentType: 'markdown' as never, emitUpdate: false })
})
// Lookups finish after the text that triggered them, so redraw once the resolved set moves.
watch(() => linkedKeys.value.join(' '), () => {
  if (editor.value) editor.value.view.dispatch(editor.value.state.tr.setMeta(ticketLinkKey, true))
})
watch(() => props.disabled, (disabled) => { editor.value?.setEditable(!disabled); if (disabled) ticket.value = null })
watch(() => [props.slug, props.projectKey], () => { ticket.value = null })
onBeforeUnmount(() => editor.value?.destroy())
</script>

<template>
  <div ref="root" class="border-input bg-background relative rounded-md border">
    <div class="border-border flex items-center gap-0.5 border-b p-1" role="toolbar" aria-label="Formatting">
      <button
        v-for="action in actions"
        :key="action.label"
        type="button"
        class="text-muted-foreground hover:text-foreground hover:bg-accent inline-flex size-7 items-center justify-center rounded disabled:opacity-50"
        :aria-label="action.label"
        :title="action.label"
        :disabled="disabled"
        @click="action.run()"
      >
        <component :is="action.icon" class="size-4" aria-hidden="true" />
      </button>
      <div
        v-if="inTable && !disabled"
        class="bg-primary text-primary-foreground ml-1 inline-flex items-center gap-0.5 rounded-md py-0.5 pr-0.5 pl-2 shadow-sm"
        role="group"
        aria-label="Table"
      >
        <span class="mr-1 text-xs font-medium">Table</span>
        <button
          v-for="action in tableActions"
          :key="action.label"
          type="button"
          class="hover:bg-primary-foreground/20 inline-flex h-6 items-center justify-center gap-0.5 rounded px-1.5 text-xs font-medium"
          :aria-label="action.label"
          :title="action.label"
          @mousedown.prevent
          @click="action.run()"
        >
          <component :is="action.icon" class="size-3.5" aria-hidden="true" />
          <span v-if="action.text">{{ action.text }}</span>
        </button>
      </div>
      <button
        v-if="slug && projectKey"
        type="button"
        class="text-muted-foreground hover:text-foreground hover:bg-accent inline-flex size-7 items-center justify-center rounded disabled:opacity-50"
        aria-label="Insert ticket reference"
        title="Insert ticket reference"
        :disabled="disabled || editor?.isActive('codeBlock') || editor?.isActive('code')"
        @mousedown.prevent
        @click="insertTicketTrigger"
      >
        <Hash class="size-4" aria-hidden="true" />
      </button>
      <template v-if="upload">
        <button
          type="button"
          class="text-muted-foreground hover:text-foreground hover:bg-accent inline-flex size-7 items-center justify-center rounded disabled:opacity-50"
          aria-label="Attach file"
          title="Attach file"
          :disabled="disabled"
          @click="fileInput?.click()"
        >
          <Paperclip class="size-4" aria-hidden="true" />
        </button>
        <input ref="fileInput" type="file" class="hidden" multiple :accept="accept" @change="pickFiles" />
        <span v-if="uploading" class="text-muted-foreground ml-auto px-2 text-xs" role="status">Uploading…</span>
      </template>
    </div>
    <EditorContent :editor="editor" />
    <div
      v-if="ticket"
      class="bg-popover text-popover-foreground absolute z-50 w-80 max-w-full overflow-hidden rounded-md border py-1 text-sm shadow-md"
      :style="{ left: `${ticket.left}px`, top: `${ticket.top}px` }"
      data-testid="ticket-list"
    >
      <p v-if="ticketLoading" class="text-muted-foreground px-2 py-1.5" role="status">Loading tickets…</p>
      <p v-else-if="ticketError" class="text-muted-foreground px-2 py-1.5" role="status">Could not load tickets.</p>
      <p v-else-if="!ticketMatches.length" class="text-muted-foreground px-2 py-1.5" role="status">No matches</p>
      <ul v-else role="listbox" aria-label="Tickets to reference">
        <li
          v-for="(item, index) in ticketMatches"
          :key="item.id"
          role="option"
          :aria-selected="index === ticketIndex"
          class="flex cursor-pointer items-center gap-2 px-2 py-1.5"
          :class="index === ticketIndex && 'bg-accent text-accent-foreground'"
          @mousedown.prevent="pickTicket(item)"
          @mouseenter="ticketIndex = index"
        >
          <span class="shrink-0 font-mono text-xs">{{ item.key }}</span>
          <span class="truncate">{{ item.title }}</span>
        </li>
      </ul>
    </div>
    <ul
      v-if="mention && mentionMatches.length"
      class="bg-popover text-popover-foreground absolute z-50 w-64 overflow-hidden rounded-md border py-1 text-sm shadow-md"
      :style="{ left: `${mention.left}px`, top: `${mention.top}px` }"
      role="listbox"
      aria-label="People to mention"
      data-testid="mention-list"
    >
      <li
        v-for="(person, index) in mentionMatches"
        :key="person.id"
        role="option"
        :aria-selected="index === mentionIndex"
        class="flex cursor-pointer items-center gap-2 px-2 py-1.5"
        :class="index === mentionIndex && 'bg-accent text-accent-foreground'"
        @mousedown.prevent="pickMention(person)"
        @mouseenter="mentionIndex = index"
      >
        <UserAvatar :name="person.name" :src="person.avatarSrc" :is-agent="person.isAgent" size="sm" />
        <span class="truncate">{{ person.name }}</span>
      </li>
    </ul>
  </div>
</template>
