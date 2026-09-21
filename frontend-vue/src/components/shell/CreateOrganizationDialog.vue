<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

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
import { useOrganizationsStore } from '@/stores/organizations'
import { ApiError } from '@/utils/api'

/**
 * Creating an organization. The slug preview is the point of the second field: it is
 * permanent — it goes into every link, and renaming later changes only the display name —
 * so the moment to see it is before pressing the button, not after.
 */
const open = defineModel<boolean>('open', { required: true })

const emit = defineEmits<{ created: [slug: string] }>()

const organizations = useOrganizationsStore()
const toast = useToast()

const name = ref('')
const slug = ref('')
const submitting = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

/**
 * Mirrors the server's derivation closely enough to be a useful preview. It is only a
 * preview: the API decides, and it is the one that resolves collisions.
 */
const derivedSlug = computed(() =>
  name.value
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 40),
)

const effectiveSlug = computed(() => slug.value.trim().toLowerCase() || derivedSlug.value)

watch(open, (isOpen) => {
  if (!isOpen) return
  name.value = ''
  slug.value = ''
  fieldErrors.value = {}
})

async function submit() {
  submitting.value = true
  fieldErrors.value = {}

  try {
    const created = await organizations.create({
      name: name.value.trim(),
      slug: slug.value.trim() ? slug.value.trim().toLowerCase() : undefined,
    })
    open.value = false
    toast.success(`${created.name} is ready.`)
    emit('created', created.slug)
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
        <DialogTitle>New organization</DialogTitle>
        <DialogDescription>
          You will be its owner. Everything in Aictiq — projects, items, agents — lives
          inside one.
        </DialogDescription>
      </DialogHeader>

      <form id="create-org" class="space-y-4" novalidate @submit.prevent="submit">
        <div class="space-y-1.5">
          <label for="org-name" class="text-sm font-medium">Name</label>
          <Input
            id="org-name"
            v-model="name"
            required
            autocomplete="organization"
            placeholder="Acme Corporation"
            :aria-invalid="Boolean(fieldErrors.name)"
          />
          <p v-for="message in fieldErrors.name" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>

        <div class="space-y-1.5">
          <label for="org-slug" class="text-sm font-medium">
            Address <span class="text-muted-foreground font-normal">(optional)</span>
          </label>
          <Input
            id="org-slug"
            v-model="slug"
            :placeholder="derivedSlug || 'acme-corporation'"
            :aria-invalid="Boolean(fieldErrors.slug)"
            aria-describedby="org-slug-hint"
          />
          <p id="org-slug-hint" class="text-muted-foreground text-xs">
            <template v-if="effectiveSlug">
              Links will look like <span class="font-mono">/orgs/{{ effectiveSlug }}</span
              >. This cannot be changed later.
            </template>
            <template v-else>Left blank, it is derived from the name.</template>
          </p>
          <p v-for="message in fieldErrors.slug" :key="message" class="text-destructive text-xs">
            {{ message }}
          </p>
        </div>
      </form>

      <DialogFooter>
        <Button type="button" variant="ghost" :disabled="submitting" @click="open = false">
          Cancel
        </Button>
        <Button type="submit" form="create-org" :disabled="submitting || !name.trim()">
          <Loader2 v-if="submitting" class="animate-spin" aria-hidden="true" />
          Create organization
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
