<script setup lang="ts">
import { computed } from 'vue'
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { useRoute } from 'vue-router'

import ItemDetail from '@/components/items/ItemDetail.vue'
import { projectKeyOf, useItemModal } from '@/composables/useItemModal'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * The item a list opened with `?item=KEY`. Mounted once by the shell, so every list gets it by
 * calling `useItemModal().open`. The close button lives in the item's own title row rather than
 * in a header strip of its own, so the dialog spends no height on chrome.
 */
const route = useRoute()
const organizations = useOrganizationsStore()
const { openKey, close } = useItemModal()

const slug = computed(() =>
  typeof route.params.slug === 'string' ? route.params.slug : (organizations.currentSlug ?? ''),
)
const projectKey = computed(() => (openKey.value ? projectKeyOf(openKey.value) : ''))
const open = computed(() => Boolean(openKey.value && slug.value && projectKey.value))
</script>

<template>
  <DialogRoot :open="open" @update:open="(value) => !value && close()">
    <DialogPortal>
      <DialogOverlay
        class="data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 fixed inset-0 z-50 bg-black/40 duration-100"
      />
      <DialogContent
        class="bg-background data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95 border-foreground/30 fixed top-1/2 left-1/2 z-50 max-h-[calc(100dvh-2rem)] w-[calc(100%-2rem)] max-w-6xl -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-xl border shadow-2xl outline-none duration-100"
      >
        <DialogTitle class="sr-only">{{ openKey }}</DialogTitle>
        <DialogDescription class="sr-only">Work item details</DialogDescription>
        <ItemDetail
          v-if="openKey"
          :key="openKey"
          :slug="slug"
          :project-key="projectKey"
          :item-key="openKey"
          modal
          @close="close"
        />
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
