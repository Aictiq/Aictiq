<script setup lang="ts">
import { ExternalLink, X } from '@lucide/vue'
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * Full-size view of an image in Markdown. Images in `.aictiq-markdown` - rendered or in the
 * editor - are drawn as thumbnails (see `assets/index.css`), so one delegated listener here
 * serves descriptions, comments and wiki pages without each of them knowing about it.
 * Mounted once by the shell.
 */
const image = ref<{ src: string; alt: string } | null>(null)

function onClick(event: MouseEvent) {
  if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey) return
  const target = event.target
  if (!(target instanceof HTMLImageElement) || !target.closest('.aictiq-markdown')) return
  // An image inside a link belongs to the link.
  if (target.closest('a')) return
  event.preventDefault()
  image.value = { src: target.currentSrc || target.src, alt: target.alt }
}

onMounted(() => document.addEventListener('click', onClick))
onBeforeUnmount(() => document.removeEventListener('click', onClick))
</script>

<template>
  <DialogRoot :open="image !== null" @update:open="(open) => !open && (image = null)">
    <DialogPortal>
      <DialogOverlay
        class="data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 fixed inset-0 z-50 bg-black/80 duration-100"
      />
      <DialogContent
        class="fixed inset-0 z-50 flex flex-col items-center justify-center gap-3 p-6 outline-none"
        @click.self="image = null"
      >
        <DialogTitle class="sr-only">{{ image?.alt || 'Image' }}</DialogTitle>
        <DialogDescription class="sr-only">Full-size image</DialogDescription>
        <img
          v-if="image"
          :src="image.src"
          :alt="image.alt"
          class="max-h-[calc(100dvh-6rem)] max-w-full rounded-md object-contain shadow-2xl"
        />
        <div class="flex items-center gap-2">
          <a
            v-if="image"
            :href="image.src"
            target="_blank"
            rel="noopener"
            class="inline-flex items-center gap-1.5 rounded bg-white/10 px-2.5 py-1.5 text-sm text-white hover:bg-white/20"
          >
            <ExternalLink class="size-4" aria-hidden="true" /> Open original
          </a>
          <DialogClose
            class="inline-flex items-center gap-1.5 rounded bg-white/10 px-2.5 py-1.5 text-sm text-white hover:bg-white/20"
          >
            <X class="size-4" aria-hidden="true" /> Close
          </DialogClose>
        </div>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
