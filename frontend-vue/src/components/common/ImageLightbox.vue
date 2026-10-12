<script setup lang="ts">
import { ChevronLeft, ChevronRight, ExternalLink, X } from '@lucide/vue'
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * Full-size view of an image in Markdown. Images in `.aictiq-markdown` - rendered or in the
 * editor - are drawn as thumbnails (see `assets/index.css`), so one delegated listener here
 * serves descriptions, comments and wiki pages without each of them knowing about it.
 * Mounted once by the shell.
 *
 * The images around the clicked one form a gallery the arrow keys, a swipe or the buttons
 * step through: every image of the nearest `[data-image-gallery]` (the comment section), or of
 * the clicked image's own Markdown block when there is none.
 */
type Image = { src: string; alt: string }
const images = ref<Image[]>([])
const index = ref(0)
const image = computed<Image | null>(() => images.value[index.value] ?? null)

/** Whether a click on this image opens the lightbox. */
function viewable(element: HTMLImageElement) {
  // An image inside a link belongs to the link.
  return element.closest('.aictiq-markdown') !== null && element.closest('a') === null
}

function describe(element: HTMLImageElement): Image {
  return { src: element.currentSrc || element.src, alt: element.alt }
}

function onClick(event: MouseEvent) {
  if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey) return
  const target = event.target
  if (!(target instanceof HTMLImageElement) || !viewable(target)) return
  event.preventDefault()
  // A draft in the editor is not part of the gallery, and does not get one either.
  const editing = target.closest('[contenteditable="true"]') !== null
  const scope = editing
    ? null
    : (target.closest('[data-image-gallery]') ?? target.closest('.aictiq-markdown'))
  const members = scope
    ? [...scope.querySelectorAll('img')].filter(
        (element) => viewable(element) && element.closest('[contenteditable="true"]') === null,
      )
    : [target]
  images.value = members.map(describe)
  index.value = Math.max(0, members.indexOf(target))
}

function close() {
  images.value = []
}

/** Moves through the gallery, wrapping around at either end. */
function step(by: -1 | 1) {
  const count = images.value.length
  if (count > 1) index.value = (index.value + by + count) % count
}

function onKeydown(event: KeyboardEvent) {
  if (event.altKey || event.metaKey || event.ctrlKey) return
  if (event.key === 'ArrowLeft') step(-1)
  else if (event.key === 'ArrowRight') step(1)
  else return
  event.preventDefault()
}

// A horizontal swipe steps through the gallery; a vertical one is left to scrolling.
let touchStart: { x: number; y: number } | null = null
function onTouchStart(event: TouchEvent) {
  const touch = event.touches[0]
  touchStart = event.touches.length === 1 && touch ? { x: touch.clientX, y: touch.clientY } : null
}
function onTouchEnd(event: TouchEvent) {
  const touch = event.changedTouches[0]
  if (!touchStart || !touch) return
  const dx = touch.clientX - touchStart.x
  const dy = touch.clientY - touchStart.y
  touchStart = null
  if (Math.abs(dx) < 50 || Math.abs(dx) < Math.abs(dy) * 1.5) return
  step(dx < 0 ? 1 : -1)
}

onMounted(() => document.addEventListener('click', onClick))
onBeforeUnmount(() => document.removeEventListener('click', onClick))
</script>

<template>
  <DialogRoot :open="image !== null" @update:open="(open) => !open && close()">
    <DialogPortal>
      <DialogOverlay
        class="data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 fixed inset-0 z-50 bg-black/80 duration-100"
      />
      <DialogContent
        class="fixed inset-0 z-50 flex cursor-pointer flex-col items-center justify-center gap-3 p-6 outline-none [&>*]:cursor-auto"
        data-testid="image-lightbox"
        @click.self="close()"
        @keydown="onKeydown"
        @touchstart.passive="onTouchStart"
        @touchend="onTouchEnd"
      >
        <DialogTitle class="sr-only">{{ image?.alt || 'Image' }}</DialogTitle>
        <DialogDescription class="sr-only">
          Full-size image<template v-if="images.length > 1"
            >, {{ index + 1 }} of {{ images.length }}. Use the left and right arrow keys to move
            between images.</template
          >
        </DialogDescription>
        <img
          v-if="image"
          :key="image.src"
          :src="image.src"
          :alt="image.alt"
          class="max-h-[calc(100dvh-6rem)] max-w-full rounded-md object-contain shadow-2xl"
        />
        <div class="flex items-center gap-2">
          <template v-if="images.length > 1">
            <button
              type="button"
              class="inline-flex items-center rounded bg-white/10 p-1.5 text-white hover:bg-white/20"
              aria-label="Previous image"
              data-testid="lightbox-previous"
              @click="step(-1)"
            >
              <ChevronLeft class="size-4" aria-hidden="true" />
            </button>
            <span class="text-sm text-white tabular-nums" data-testid="lightbox-position"
              >{{ index + 1 }} / {{ images.length }}</span
            >
            <button
              type="button"
              class="inline-flex items-center rounded bg-white/10 p-1.5 text-white hover:bg-white/20"
              aria-label="Next image"
              data-testid="lightbox-next"
              @click="step(1)"
            >
              <ChevronRight class="size-4" aria-hidden="true" />
            </button>
          </template>
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
