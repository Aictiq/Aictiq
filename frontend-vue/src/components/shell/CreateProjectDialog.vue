<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import { suggestKey, type ProjectVisibility } from '@/api/projects'
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
import { useProjectsStore } from '@/stores/projects'
import { ApiError } from '@/utils/api'

/**
 * Creating a project. The key preview is the point of the second field: it is permanent -
 * it starts every item id the team will ever quote, in commits, in chat, in an agent's
 * configuration - so the moment to see it is before pressing the button.
 */
const open = defineModel<boolean>('open', { required: true })

const emit = defineEmits<{ created: [key: string] }>()

const projects = useProjectsStore()
const toast = useToast()

const name = ref('')
const key = ref('')
const visibility = ref<ProjectVisibility>('organization')
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

const derivedKey = computed(() => suggestKey(name.value))
const effectiveKey = computed(() => key.value.trim().toUpperCase() || derivedKey.value)

watch(open, (isOpen) => {
  if (!isOpen) return
  name.value = ''
  key.value = ''
  visibility.value = 'organization'
  fieldErrors.value = {}
})

async function submit() {
  submitting.value = true
  fieldErrors.value = {}

  try {
    const created = await projects.create({
      name: name.value.trim(),
      key: key.value.trim() ? key.value.trim().toUpperCase() : undefined,
      visibility: visibility.value,
    })
    open.value = false
    toast.success(`${created.name} is ready.`)
    emit('created', created.key)
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
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="sm:max-w-md">
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
