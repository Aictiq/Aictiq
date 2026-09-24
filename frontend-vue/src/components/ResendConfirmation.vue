<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { ref, useTemplateRef } from 'vue'

import { resendConfirmation } from '@/api/auth'
import TurnstileWidget from '@/components/TurnstileWidget.vue'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/utils/api'
import { turnstileReady } from '@/utils/turnstile'

/**
 * "Send the confirmation link again", for an account that registered and has not yet
 * followed its link. Used where that fact is known: right after registering, and on the
 * sign-in form once the password has been accepted.
 *
 * The API answers identically whatever is behind the address and sends at most one mail a
 * minute per account - so the page cannot know whether this click minted a new link or
 * the last one is still the live one, and the wording covers both.
 */
const props = defineProps<{ email: string }>()

const captcha = ref<string | null>(null)
const widget = useTemplateRef<InstanceType<typeof TurnstileWidget>>('widget')
const sending = ref(false)
const sent = ref(false)
const failed = ref<string | null>(null)

async function resend() {
  sending.value = true
  failed.value = null
  try {
    await resendConfirmation(props.email, captcha.value)
    sent.value = true
  } catch (error) {
    failed.value = error instanceof ApiError ? error.title : 'Something went wrong.'
  } finally {
    sending.value = false
    widget.value?.reset()
  }
}
</script>

<template>
  <div class="space-y-3">
    <p v-if="sent" role="status" class="text-muted-foreground text-center text-sm">
      A link is on its way to {{ email }}. If more than one arrives, use the newest - it is
      the only one that works.
    </p>
    <template v-else>
      <TurnstileWidget ref="widget" v-model:token="captcha" action="resend-confirmation" />
      <!-- type="button": this sits inside the sign-in form, and must not submit it. -->
      <Button
        type="button"
        variant="outline"
        class="w-full"
        :disabled="sending || !turnstileReady(captcha)"
        @click="resend"
      >
        <Loader2 v-if="sending" class="size-4 animate-spin" aria-hidden="true" />
        Send the link again
      </Button>
    </template>
    <p v-if="failed" role="alert" class="text-destructive text-xs">{{ failed }}</p>
  </div>
</template>
