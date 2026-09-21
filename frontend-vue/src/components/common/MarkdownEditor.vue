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

import { useToast } from '@/composables/useToast'
import { planLimitMessage } from '@/lib/billing'
import { ApiError } from '@/utils/api'

/**
 * `upload` turns a file into a same-origin URL (see `api/attachments.ts`). Without it the
 * editor accepts no files — pasting an image stays the browser's business, which for
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
  }>(),
  { placeholder: 'Write a description…', disabled: false, compact: false, upload: undefined, accept: undefined },
)
const emit = defineEmits<{ 'update:modelValue': [markdown: string]; blur: [] }>()
const toast = useToast()
const uploading = ref(0)
const fileInput = ref<HTMLInputElement | null>(null)
defineExpose({ uploading: computed(() => uploading.value > 0) })

const editor = useEditor({
  content: props.modelValue,
  contentType: 'markdown',
  editable: !props.disabled,
  extensions: [StarterKit, TaskList, TaskItem.configure({ nested: true }), Image, Table.configure({ resizable: true }), TableRow, TableHeader, TableCell, Markdown],
  editorProps: {
    attributes: {
      class: `aictiq-markdown ${props.compact ? 'min-h-20' : 'min-h-44'} px-3 py-2 text-sm outline-none`,
      'data-placeholder': props.placeholder,
    },
    // ProseMirror binds Escape to "select parent node" and marks the key handled, which a
    // surrounding dialog reads as "do not close". Escape in a text box means "leave", so
    // the editor steps aside: true here skips ProseMirror without preventing the default.
    handleDOMEvents: { keydown: (_view, event) => event.key === 'Escape' },
    handlePaste: (_view, event) => insertFiles(event.clipboardData?.files),
    handleDrop: (view, event) => {
      const at = view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos
      return insertFiles(event.dataTransfer?.files, at)
    },
  },
  onUpdate: ({ editor: current }) => emit('update:modelValue', current.getMarkdown()),
  // Leaving the editor to pick a file is not "done editing" — a blur-save would race the upload.
  onBlur: () => { if (uploading.value === 0) emit('blur') },
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
  <div class="border-input bg-background rounded-md border">
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
  </div>
</template>
