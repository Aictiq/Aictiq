<script setup lang="ts">
import Image from '@tiptap/extension-image'
import { Table } from '@tiptap/extension-table'
import TableCell from '@tiptap/extension-table-cell'
import TableHeader from '@tiptap/extension-table-header'
import TableRow from '@tiptap/extension-table-row'
import TaskItem from '@tiptap/extension-task-item'
import TaskList from '@tiptap/extension-task-list'
import { Markdown } from '@tiptap/markdown'
import StarterKit from '@tiptap/starter-kit'
import { EditorContent, useEditor } from '@tiptap/vue-3'
import { Bold, Code, List, ListTodo, Minus, Paperclip, Plus, Table as TableIcon, Trash2 } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'

import UserAvatar from '@/components/common/UserAvatar.vue'
import { useToast } from '@/composables/useToast'
import { mentionToken, type Mentionable, searchMentionables } from '@/lib/mentions'
import { planLimitMessage } from '@/lib/billing'
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
  }>(),
  {
    placeholder: 'Write a description…',
    disabled: false,
    compact: false,
    upload: undefined,
    accept: undefined,
    mentionables: undefined,
    autofocus: false,
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
  if (!empty || $from.parent.type.spec.code) return void (mention.value = null)
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

const editor = useEditor({
  content: props.modelValue,
  contentType: 'markdown',
  editable: !props.disabled,
  autofocus: props.autofocus ? 'end' : false,
  extensions: [StarterKit, TaskList, TaskItem.configure({ nested: true }), Image, Table.configure({ resizable: true }), TableRow, TableHeader, TableCell, Markdown],
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
        if (mention.value) {
          mention.value = null
          event.preventDefault()
          event.stopPropagation()
        }
        return true
      },
    },
    handleKeyDown: (_view, event) => mentionKey(event),
    handlePaste: (_view, event) => insertFiles(event.clipboardData?.files),
    handleDrop: (view, event) => {
      const at = view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos
      return insertFiles(event.dataTransfer?.files, at)
    },
  },
  onUpdate: ({ editor: current }) => emit('update:modelValue', current.getMarkdown()),
  onSelectionUpdate: trackMention,
  onTransaction: trackMention,
  // Leaving the editor to pick a file is not "done editing" - a blur-save would race the upload.
  onBlur: () => {
    mention.value = null
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
watch(() => props.disabled, (disabled) => editor.value?.setEditable(!disabled))
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
