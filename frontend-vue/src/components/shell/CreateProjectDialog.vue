<script setup lang="ts">
import { Bot, FolderGit2, Loader2, NotebookPen, Server } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import { suggestKey, type Project, type ProjectVisibility } from '@/api/projects'
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
import { useToast } from '@/composables/useToast'
import { factorySetupPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { ApiError } from '@/utils/api'

/**
 * Creating a project. The key preview is the point of the second field: it is permanent -
 * it starts every item id the team will ever quote, in commits, in chat, in an agent's
 * configuration - so the moment to see it is before pressing the button.
 *
 * For someone who operates the factory, a new project is also one that no agent can work on
 * yet, so the dialog stays open on what it still needs and offers the setup guide.
 */
const open = defineModel<boolean>('open', { required: true })

const emit = defineEmits<{ created: [key: string] }>()

const projects = useProjectsStore()
const organizations = useOrganizationsStore()
const router = useRouter()
const toast = useToast()

/** Set once the project exists and the dialog is showing what AI work on it needs. */
const created = ref<Project | null>(null)

const needs = [
  { icon: Bot, label: 'An agent on the project' },
  { icon: FolderGit2, label: 'Where its code lives' },
  { icon: Server, label: 'A runner that can reach it' },
  { icon: NotebookPen, label: 'A playbook' },
]

const name = ref('')
const key = ref('')
const visibility = ref<ProjectVisibility>('organization')
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

const derivedKey = computed(() => suggestKey(name.value))
const effectiveKey = computed(() => key.value.trim().toUpperCase() || derivedKey.value)

watch(open, (isOpen) => {
  if (!isOpen) {
    // Closed from the follow-up any way but the guide: carry on as without it.
    if (created.value) emit('created', created.value.key)
    created.value = null
    return
  }
  created.value = null
  name.value = ''
  key.value = ''
  visibility.value = 'organization'
  fieldErrors.value = {}
})

async function submit() {
  submitting.value = true
  fieldErrors.value = {}

  try {
    const project = await projects.create({
      name: name.value.trim(),
      key: key.value.trim() ? key.value.trim().toUpperCase() : undefined,
      visibility: visibility.value,
    })
    if (organizations.current?.canOperateFactory) {
      created.value = project
      return
    }
    open.value = false
    toast.success(`${project.name} is ready.`)
    emit('created', project.key)
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      toast.error(error)
    }
  } finally {
    submitting.value = false
  }
}

function openSetup() {
  const project = created.value
  const slug = organizations.currentSlug
  if (!project || !slug) return
  created.value = null
  open.value = false
  void router.push(factorySetupPath(slug, { project: project.key }))
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent v-if="created" class="sm:max-w-md" data-testid="project-created-next">
      <DialogHeader>
        <DialogTitle>{{ created.name }} is ready</DialogTitle>
        <DialogDescription>
          People can start adding items now. For an agent to work on them, the Factory needs four
          things - the setup guide walks through each one and checks it for you.
        </DialogDescription>
      </DialogHeader>
      <ol class="grid grid-cols-2 gap-2">
        <li
          v-for="(need, index) in needs"
          :key="need.label"
          class="project-need border-border flex items-center gap-2 rounded-lg border px-2.5 py-2 text-xs"
          :style="{ animationDelay: `${index * 90}ms` }"
        >
          <component :is="need.icon" class="text-primary size-4 flex-none" aria-hidden="true" />
          {{ need.label }}
        </li>
      </ol>
      <DialogFooter>
        <Button type="button" variant="ghost" @click="open = false">Go to the project</Button>
        <Button type="button" data-testid="project-open-setup" @click="openSetup">Set up AI work</Button>
      </DialogFooter>
    </DialogContent>

    <DialogContent v-else class="sm:max-w-md">
      <DialogHeader>
        <DialogTitle>New project</DialogTitle>
        <DialogDescription>
          A project has its own items, board, workflow and wiki. You will be its admin.
        </DialogDescription>
      </DialogHeader>

      <form id="create-project" class="space-y-4" novalidate @submit.prevent="submit">
        <div class="space-y-1.5">
          <label for="project-name" class="text-sm font-medium">Name</label>
          <Input
            id="project-name"
            v-model="name"
            required
            placeholder="Acme Website"
            :aria-invalid="Boolean(fieldErrors.name)"
          />
          <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="project-key" class="text-sm font-medium">
            Key <span class="text-muted-foreground font-normal">(optional)</span>
          </label>
          <Input
            id="project-key"
            v-model="key"
            class="font-mono uppercase"
            :placeholder="derivedKey || 'AW'"
            :aria-invalid="Boolean(fieldErrors.key)"
            aria-describedby="project-key-hint"
          />
          <p id="project-key-hint" class="text-muted-foreground text-xs">
            <template v-if="effectiveKey">
              Items will be numbered
              <span class="font-mono">{{ effectiveKey }}-1</span>,
              <span class="font-mono">{{ effectiveKey }}-2</span>. This cannot be changed
              later.
            </template>
            <template v-else>Left blank, it is suggested from the name.</template>
          </p>
          <p v-for="message in fieldErrors.key" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <fieldset class="space-y-1.5">
          <legend class="text-sm font-medium">Who can see it</legend>
          <label
            v-for="option in (['organization', 'private'] as const)"
            :key="option"
            class="border-border hover:bg-accent/60 flex cursor-pointer items-start gap-2 rounded-lg border p-2.5"
            :class="visibility === option && 'border-primary'"
          >
            <input
              v-model="visibility"
              type="radio"
              name="project-visibility"
              :value="option"
              class="mt-0.5"
            />
            <span class="text-[12.5px]">
              <span class="block font-medium capitalize">{{
                option === 'organization' ? 'Everyone in the organization' : 'Only invited people'
              }}</span>
              <span class="text-muted-foreground">
                {{
                  option === 'organization'
                    ? 'Members can open it; guests can read and comment.'
                    : 'Nobody sees it until you add them - apart from owners and admins.'
                }}
              </span>
            </span>
          </label>
        </fieldset>
      </form>

      <DialogFooter>
        <Button type="button" variant="ghost" :disabled="submitting" @click="open = false">
          Cancel
        </Button>
        <Button type="submit" form="create-project" :disabled="submitting || !name.trim()">
          <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
          Create project
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.project-need {
  animation: project-need-in 0.4s ease-out both;
}

@keyframes project-need-in {
  from {
    opacity: 0;
    transform: translateY(6px);
  }
}
</style>
