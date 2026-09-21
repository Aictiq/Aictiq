<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { ref } from 'vue'
import { RouterLink } from 'vue-router'

import AuthCard from '@/components/AuthCard.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { forgotPassword } from '@/api/profile'
import { ApiError } from '@/utils/api'

/**
 * Asks for a reset link.
 *
 * The answer is the same whether or not the address has an account behind it, and this
 * page says so out loud rather than pretending to have looked. A form that said "no such
 * account" would be a way to ask, one address at a time, who is on this instance — and
 * being told your own address is unknown is a worse experience than being told to check
 * your inbox.
 */
const email = ref('')
const sending = ref(false)
const sent = ref(false)
const configured = ref(true)
const fieldErrors = ref<Record<string, string[]>>({})
const failed = ref<string | null>(null)

async function submit() {
  sending.value = true
  fieldErrors.value = {}
  failed.value = null

  try {
    const result = await forgotPassword(email.value.trim())
    configured.value = result.emailConfigured
    sent.value = true
  } catch (error) {
    if (error instanceof ApiError && Object.keys(error.fieldErrors).length > 0) {
      fieldErrors.value = error.fieldErrors
    } else {
      failed.value = error instanceof ApiError ? error.title : 'Something went wrong.'
    }
  } finally {
    sending.value = false
  }
}
</script>

<template>
  <AuthCard
    v-if="sent && configured"
    title="Check your inbox"
    description="If that address has an account, a link to choose a new password is on its way. It works once and expires in an hour."
  >
    <template #footer>
      <RouterLink to="/login" class="text-foreground font-medium underline underline-offset-4">
        Back to sign in
      </RouterLink>
    </template>
  </AuthCard>

  <AuthCard
    v-else-if="sent"
    title="This instance cannot send email"
    description="No SMTP relay is configured here, so no reset link can reach you. Ask an administrator to set a new password for you."
  >
    <template #footer>
      <RouterLink to="/login" class="text-foreground font-medium underline underline-offset-4">
        Back to sign in
      </RouterLink>
    </template>
  </AuthCard>

  <AuthCard
    v-else
    title="Reset your password"
    description="Enter the address you sign in with and we will send a link to choose a new password."
  >
    <form class="space-y-4" novalidate @submit.prevent="submit">
      <div class="space-y-1.5">
        <label for="forgot-email" class="text-sm font-medium">Email</label>
        <Input
          id="forgot-email"
          v-model="email"
          type="email"
          autocomplete="email"
          required
          :aria-invalid="Boolean(fieldErrors.email)"
        />
        <p v-for="message in fieldErrors.email" :key="message" class="text-destructive text-xs">
          {{ message }}
        </p>
      </div>

      <p v-if="failed" class="text-destructive text-xs">{{ failed }}</p>

      <Button type="submit" class="w-full" :disabled="sending || email.trim().length === 0">
        <Loader2 v-if="sending" class="animate-spin" aria-hidden="true" />
        Send the link
      </Button>
    </form>

    <template #footer>
      <RouterLink to="/login" class="text-foreground font-medium underline underline-offset-4">
        Back to sign in
      </RouterLink>
    </template>
  </AuthCard>
</template>
