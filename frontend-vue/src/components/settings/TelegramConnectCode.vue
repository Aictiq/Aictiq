<script setup lang="ts">
import { computed } from 'vue'

import { Button } from '@/components/ui/button'

/**
 * The one-time code that ties a Telegram chat to an Aictiq channel. The bot learns the
 * chat from whoever sends it the code, so the code is the whole handshake - shown with the
 * exact command to send and the deep link that pre-fills it.
 *
 * In a group Telegram addresses the command to one bot, so `/start@bot code` is offered
 * alongside the plain form: both reach the bot, and people paste whichever they see first.
 */
const props = defineProps<{
  code: string
  url: string | null
  expiresAt: string | null
  botUsername: string | null
  group?: boolean
}>()

const bot = computed(() => (props.botUsername ? `@${props.botUsername}` : 'the Aictiq bot'))
const expires = computed(() =>
  props.expiresAt ? new Date(props.expiresAt).toLocaleTimeString([], { timeStyle: 'short' }) : null,
)
</script>

<template>
  <div class="bg-muted/40 rounded border p-3 text-sm" data-testid="telegram-connect-code">
    <p v-if="group">
      Add <strong>{{ bot }}</strong> to the group and send
      <code class="font-mono">/start {{ code }}</code> in it.
    </p>
    <p v-else>
      Send <code class="font-mono">/start {{ code }}</code> to <strong>{{ bot }}</strong
      >.
    </p>
    <p v-if="group && botUsername" class="text-muted-foreground mt-1 text-xs">
      If Telegram asks which bot you mean, send
      <code class="font-mono">/start@{{ botUsername }} {{ code }}</code> instead.
    </p>
    <div class="mt-2 flex flex-wrap items-center gap-3">
      <Button v-if="url" as="a" :href="url" target="_blank" rel="noopener" size="sm">
        Open Telegram
      </Button>
      <span class="text-muted-foreground text-xs">
        Waiting for the bot…<template v-if="expires"> The code expires at {{ expires }}.</template>
      </span>
    </div>
  </div>
</template>
