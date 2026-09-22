<script setup lang="ts">
import { h, ref } from 'vue'

import type { Label } from '@/api/labels'
import DataTable from '@/components/common/DataTable.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import InlineEdit from '@/components/common/InlineEdit.vue'
import KeyChip from '@/components/common/KeyChip.vue'
import LabelChip from '@/components/common/LabelChip.vue'
import LabelPicker from '@/components/common/LabelPicker.vue'
import Markdown from '@/components/common/Markdown.vue'
import PriorityIcon, { type Priority } from '@/components/common/PriorityIcon.vue'
import StateBadge, { type StateCategory } from '@/components/common/StateBadge.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import AppShell from '@/components/shell/AppShell.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import type { AictiqColumnDef } from '@/lib/table'
import { useUiStore } from '@/stores/ui'

/**
 * Every component, every state, on one page. It exists so a visual change can be judged
 * against the whole set rather than against whichever screen happened to be open, and so
 * an accessibility pass has somewhere to run.
 *
 * Development only - it is not registered in a production build.
 */
const ui = useUiStore()

const priorities: Priority[] = ['urgent', 'high', 'medium', 'low', 'none']
const categories: StateCategory[] = ['proposed', 'active', 'resolved', 'completed', 'removed']

const editable = ref('Click me to edit in place')

const sampleLabels: Label[] = [
  { id: 'l1', name: 'frontend', color: '#eda45c', description: null, group: 'type', itemCount: 4, version: 1 },
  { id: 'l2', name: 'backend', color: '#6aa9d8', description: null, group: 'type', itemCount: 2, version: 1 },
  { id: 'l3', name: 'urgent', color: '#d97757', description: null, group: null, itemCount: 1, version: 1 },
  { id: 'l4', name: 'good first issue', color: null, description: null, group: null, itemCount: 0, version: 1 },
]
const pickedLabels = ref<string[]>(['l1'])

interface Row {
  key: string
  title: string
  priority: Priority
  state: { name: string; category: StateCategory }
  assignee: { name: string; isAgent: boolean }
}

const rows: Row[] = [
  {
    key: 'ACME-101',
    title: 'Sign-in loses the return URL',
    priority: 'urgent',
    state: { name: 'In progress', category: 'active' },
    assignee: { name: 'Mira Kalb', isAgent: false },
  },
  {
    key: 'ACME-102',
    title: 'Board columns should remember their width',
    priority: 'medium',
    state: { name: 'To do', category: 'proposed' },
    assignee: { name: 'Claude Agent', isAgent: true },
  },
  {
    key: 'ACME-103',
    title: 'Export a sprint as CSV',
    priority: 'low',
    state: { name: 'Done', category: 'completed' },
    assignee: { name: 'Tom Reyes', isAgent: false },
  },
]

const columns: AictiqColumnDef<Row>[] = [
  {
    accessorKey: 'key',
    header: 'Key',
    cell: ({ row }) => h(KeyChip, { label: row.original.key }),
  },
  { accessorKey: 'title', header: 'Title' },
  {
    accessorKey: 'priority',
    header: 'Priority',
    cell: ({ row }) => h(PriorityIcon, { priority: row.original.priority }),
  },
  {
    accessorKey: 'state',
    header: 'State',
    cell: ({ row }) => h(StateBadge, { ...row.original.state }),
  },
  {
    accessorKey: 'assignee',
    header: 'Assignee',
    cell: ({ row }) => h(UserAvatar, { ...row.original.assignee, size: 'sm' }),
  },
]

const markdown = `## Markdown renderer

Ordinary **bold**, _italic_, \`inline code\` and a [link](https://example.com).

- Lists
- Of things

> A quotation, for the wiki.

\`\`\`ts
const claimed = await claim(itemId, expectedVersion)
\`\`\`

| Column | Meaning |
| --- | --- |
| Sanitised | Always |

Raw HTML is refused, so <img src=x onerror=alert(1)> renders as text rather than script.
`
</script>

<template>
  <AppShell>
    <div class="mx-auto max-w-4xl space-y-10 p-6">
      <header class="space-y-1">
        <h1 class="text-xl font-semibold tracking-tight">Kitchen sink</h1>
        <p class="text-muted-foreground text-[12.5px]">
          Every shared component and state. Development only.
        </p>
      </header>

      <section aria-labelledby="ks-theme" class="space-y-3">
        <h2 id="ks-theme" class="font-label">Theme and density</h2>
        <div class="flex flex-wrap items-center gap-2">
          <Button
            v-for="option in ['light', 'dark', 'system'] as const"
            :key="option"
            :variant="ui.theme === option ? 'default' : 'outline'"
            size="sm"
            @click="ui.setTheme(option)"
          >
            {{ option }}
          </Button>
          <span class="text-muted-foreground text-xs">resolved: {{ ui.resolvedTheme }}</span>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <Button
            v-for="option in ['comfortable', 'compact'] as const"
            :key="option"
            :variant="ui.density === option ? 'default' : 'outline'"
            size="sm"
            @click="ui.setDensity(option)"
          >
            {{ option }}
          </Button>
        </div>
      </section>

      <section aria-labelledby="ks-buttons" class="space-y-3">
        <h2 id="ks-buttons" class="font-label">Buttons and inputs</h2>
        <div class="flex flex-wrap items-center gap-2">
          <Button>Primary</Button>
          <Button variant="secondary">Secondary</Button>
          <Button variant="outline">Outline</Button>
          <Button variant="ghost">Ghost</Button>
          <Button variant="destructive">Destructive</Button>
          <Button disabled>Disabled</Button>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <Input placeholder="An input" class="max-w-56" />
          <Input placeholder="Invalid" aria-invalid="true" class="max-w-56" />
          <Badge>Badge</Badge>
          <Badge variant="secondary">Secondary</Badge>
        </div>
      </section>

      <section aria-labelledby="ks-atoms" class="space-y-3">
        <h2 id="ks-atoms" class="font-label">Item atoms</h2>
        <div class="flex flex-wrap items-center gap-4">
          <span
            v-for="priority in priorities"
            :key="priority"
            class="flex items-center gap-1.5 text-xs"
          >
            <PriorityIcon :priority="priority" />{{ priority }}
          </span>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <StateBadge
            v-for="category in categories"
            :key="category"
            :name="category"
            :category="category"
          />
        </div>
        <div class="flex flex-wrap items-center gap-3">
          <UserAvatar name="Mira Kalb" size="sm" />
          <UserAvatar name="Tom Reyes" />
          <UserAvatar name="Claude Agent" is-agent size="lg" />
          <KeyChip label="ACME-123" />
          <KeyChip binding="mod+k" />
          <KeyChip binding="g then i" />
        </div>
      </section>

      <section aria-labelledby="ks-inline" class="space-y-3">
        <h2 id="ks-inline" class="font-label">Inline edit</h2>
        <div class="max-w-sm">
          <InlineEdit v-model="editable" label="Example title" placeholder="Add a title" />
        </div>
      </section>

      <section aria-labelledby="ks-labels" class="space-y-3">
        <h2 id="ks-labels" class="font-label">Labels</h2>
        <div class="flex flex-wrap items-center gap-2">
          <LabelChip
            v-for="label in sampleLabels"
            :key="label.id"
            :name="label.name"
            :color="label.color"
            :group="label.group"
          />
          <LabelChip name="removable" color="#a882d9" removable @remove="() => {}" />
        </div>
        <div class="max-w-sm">
          <LabelPicker
            v-model="pickedLabels"
            :labels="sampleLabels"
            slug="acme"
            project-key="WEB"
          />
        </div>
      </section>

      <section aria-labelledby="ks-table" class="space-y-3">
        <h2 id="ks-table" class="font-label">Data table (↑ ↓ to move, Enter to open)</h2>
        <DataTable :data="rows" :columns="columns" :row-key="(row) => row.key" />
      </section>

      <section aria-labelledby="ks-empty" class="space-y-3">
        <h2 id="ks-empty" class="font-label">Empty state</h2>
        <div class="border-border rounded-lg border">
          <EmptyState
            title="No items match this filter"
            description="Try clearing the search."
            icon="◇"
          >
            <Button size="sm" variant="outline">Clear filters</Button>
          </EmptyState>
        </div>
      </section>

      <section aria-labelledby="ks-markdown" class="space-y-3">
        <h2 id="ks-markdown" class="font-label">Markdown</h2>
        <div class="border-border rounded-lg border p-4">
          <Markdown :source="markdown" />
        </div>
      </section>
    </div>
  </AppShell>
</template>
