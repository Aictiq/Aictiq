<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'

import {
  createItemTemplate,
  deleteItemTemplate,
  listItemTemplates,
  updateItemTemplate,
  type ItemTemplate,
  type WorkItemPriority,
  type WorkItemType,
} from '@/api/templates'
import { listLabels, type Label } from '@/api/labels'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'

const project = useProjectScope()
const toast = useToast()
const templates = ref<ItemTemplate[]>([])
const labels = ref<Label[]>([])
const loading = ref(true)
const saving = ref(false)
const editing = ref<ItemTemplate | null>(null)
const form = ref({ type: 'bug' as WorkItemType, name: '', descriptionMarkdown: '', defaultLabelIds: [] as string[], defaultPriority: '' as WorkItemPriority | '', isDefault: false })

function reset(template: ItemTemplate | null = null) {
  editing.value = template
  form.value = template
    ? { type: template.type, name: template.name, descriptionMarkdown: template.descriptionMarkdown, defaultLabelIds: [...template.defaultLabelIds], defaultPriority: template.defaultPriority ?? '', isDefault: template.isDefault }
    : { type: 'bug', name: '', descriptionMarkdown: '', defaultLabelIds: [], defaultPriority: '', isDefault: false }
}

async function load() {
  loading.value = true
  try {
    ;[templates.value, labels.value] = await Promise.all([
      listItemTemplates(project.slug.value, project.projectKey.value),
      listLabels(project.slug.value, project.projectKey.value),
    ])
  } catch (error) { toast.error(error) } finally { loading.value = false }
}

async function save() {
  if (!form.value.name.trim()) return
  saving.value = true
  const body = {
    ...(editing.value ? {} : { type: form.value.type }), name: form.value.name, descriptionMarkdown: form.value.descriptionMarkdown,
    defaultLabelIds: form.value.defaultLabelIds, defaultPriority: form.value.defaultPriority || null,
    isDefault: form.value.isDefault, ...(editing.value ? { version: editing.value.version } : {}),
  }
  try {
    const saved = editing.value
      ? await updateItemTemplate(project.slug.value, project.projectKey.value, editing.value.id, body)
      : await createItemTemplate(project.slug.value, project.projectKey.value, body)
    const index = templates.value.findIndex((template) => template.id === saved.id)
    if (index < 0) templates.value.push(saved); else templates.value.splice(index, 1, saved)
    reset()
  } catch (error) { toast.error(error) } finally { saving.value = false }
}

async function remove(template: ItemTemplate) {
  try { await deleteItemTemplate(project.slug.value, project.projectKey.value, template.id); templates.value = templates.value.filter((candidate) => candidate.id !== template.id) }
  catch (error) { toast.error(error) }
}

watch([project.slug, project.projectKey], load, { immediate: true })
onMounted(() => reset())
</script>

<template>
  <SettingsSection wide title="Item templates" description="Start Bugs and Stories with the structure your project expects.">
    <UiPageState v-if="loading" state="loading" />
    <div v-else class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
      <div class="grid gap-2">
        <article v-for="template in templates" :key="template.id" class="border-border rounded-lg border p-3">
          <div class="flex items-start justify-between gap-3"><div><p class="text-sm font-medium">{{ template.name }} <span class="text-muted-foreground font-normal">· {{ template.type }}</span></p><p v-if="template.isDefault" class="text-muted-foreground mt-0.5 text-xs">Default for {{ template.type }}</p></div><div class="flex gap-1"><Button size="sm" variant="ghost" @click="reset(template)">Edit</Button><Button size="sm" variant="ghost" @click="remove(template)">Delete</Button></div></div>
          <pre class="text-muted-foreground mt-2 whitespace-pre-wrap text-xs">{{ template.descriptionMarkdown }}</pre>
        </article>
      </div>
      <form class="border-border grid h-fit gap-3 rounded-lg border p-3" @submit.prevent="save">
        <h3 class="text-sm font-medium">{{ editing ? 'Edit template' : 'New template' }}</h3>
        <select v-if="!editing" v-model="form.type" class="border-input bg-background h-9 rounded border px-2 text-sm" aria-label="Item type"><option value="bug">Bug</option><option value="story">Story</option><option value="task">Task</option><option value="feature">Feature</option><option value="epic">Epic</option></select>
        <input v-model="form.name" class="border-input bg-background h-9 rounded border px-2 text-sm" placeholder="Template name" aria-label="Template name" />
        <textarea v-model="form.descriptionMarkdown" class="border-input bg-background min-h-40 rounded border p-2 font-mono text-xs" placeholder="Markdown description" aria-label="Template description" />
        <select v-model="form.defaultPriority" class="border-input bg-background h-9 rounded border px-2 text-sm" aria-label="Default priority"><option value="">No default priority</option><option value="none">None</option><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="urgent">Urgent</option></select>
        <fieldset class="grid gap-1"><legend class="text-xs font-medium">Default labels</legend><label v-for="label in labels" :key="label.id" class="flex items-center gap-2 text-xs"><input v-model="form.defaultLabelIds" type="checkbox" :value="label.id" /> {{ label.name }}</label></fieldset>
        <label class="flex items-center gap-2 text-xs"><input v-model="form.isDefault" type="checkbox" /> Default for this type</label>
        <div class="flex gap-2"><Button type="submit" size="sm" :disabled="saving">{{ saving ? 'Saving…' : 'Save template' }}</Button><Button v-if="editing" type="button" size="sm" variant="ghost" @click="reset()">Cancel</Button></div>
      </form>
    </div>
  </SettingsSection>
</template>
