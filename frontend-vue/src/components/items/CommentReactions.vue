<script setup lang="ts">
import { ref } from 'vue'
import { onClickOutside } from '@vueuse/core'
import { useQueryClient } from '@tanstack/vue-query'
import { SmilePlus } from '@lucide/vue'
import {
  commentReactionOptions,
  reactToComment,
  unreactFromComment,
  type WorkItemComment,
} from '@/api/comments'
import { useToast } from '@/composables/useToast'

const props = defineProps<{
  slug: string
  itemKey: string
  comment: WorkItemComment
  canReact: boolean
}>()
const client = useQueryClient()
const toast = useToast()
const picker = ref<HTMLElement | null>(null)
const open = ref(false)
const saving = ref(false)
onClickOutside(picker, () => {
  open.value = false
})

function label(emoji: string) {
  return commentReactionOptions.find((option) => option.emoji === emoji)?.label ?? emoji
}

async function toggle(emoji: string) {
  if (!props.canReact || saving.value) return
  saving.value = true
  open.value = false
  try {
    const reacted = props.comment.reactions.some(
      (reaction) => reaction.emoji === emoji && reaction.reactedByMe,
    )
    const change = reacted ? unreactFromComment : reactToComment
    await change(props.slug, props.itemKey, props.comment.id, emoji)
    await client.invalidateQueries({ queryKey: [props.slug, props.itemKey, 'comments'] })
  } catch (error) {
    toast.saveFailed(error, 'Your reaction could not be saved.')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="mt-1 flex items-center gap-3">
    <slot />
    <div v-if="canReact" ref="picker" class="relative" @keydown.esc="open = false">
      <button
        type="button"
        class="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-xs disabled:opacity-50"
        aria-label="Add reaction"
        :aria-expanded="open"
        :disabled="saving"
        @click="open = !open"
      >
        <SmilePlus class="size-3.5" aria-hidden="true" /> React
      </button>
      <div
        v-if="open"
        class="bg-popover text-popover-foreground absolute left-0 z-10 mt-1 flex gap-1 rounded-md border p-1 shadow-md"
        role="group"
        aria-label="Choose a reaction"
      >
        <button
          v-for="option in commentReactionOptions"
          :key="option.emoji"
          type="button"
          class="hover:bg-accent rounded px-2 py-1 text-lg focus-visible:outline-2 focus-visible:outline-offset-2"
          :aria-label="option.label"
          :aria-pressed="
            comment.reactions.some(
              (reaction) => reaction.emoji === option.emoji && reaction.reactedByMe,
            )
          "
          @click="toggle(option.emoji)"
        >
          {{ option.emoji }}
        </button>
      </div>
    </div>
  </div>
  <div
    v-if="comment.reactions.length"
    class="mt-2 flex flex-wrap gap-1"
    aria-label="Comment reactions"
  >
    <button
      v-for="reaction in comment.reactions"
      :key="reaction.emoji"
      type="button"
      class="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs"
      :class="
        reaction.reactedByMe
          ? 'border-primary/40 bg-primary/10 text-primary'
          : 'border-border text-muted-foreground'
      "
      :title="reaction.users.map((user) => user.displayName).join(', ')"
      :aria-label="`${label(reaction.emoji)}: ${reaction.count}${reaction.reactedByMe ? ', you reacted' : ''}`"
      :aria-pressed="reaction.reactedByMe"
      :disabled="!canReact || saving"
      @click="toggle(reaction.emoji)"
    >
      <span aria-hidden="true">{{ reaction.emoji }}</span> {{ reaction.count }}
    </button>
  </div>
</template>
