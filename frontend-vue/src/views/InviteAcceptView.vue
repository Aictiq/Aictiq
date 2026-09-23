<script setup lang="ts">
import { Loader2 } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import {
  acceptInvitation,
  previewInvitation,
  type InvitationPreview,
} from '@/api/invitations'
import AuthCard from '@/components/AuthCard.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useToast } from '@/composables/useToast'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'
import { ApiError } from '@/utils/api'

/**
 * The page an invitation link lands on. Public, because the whole point is that the
 * person may not have an account yet - the route does not require auth, and signing in
 * is one of the things this page offers rather than something it demands first.
 *
 * The preview is fetched before anything else so a stranger sees *who invited them to
 * what* before being asked to sign in. Being sent to a login form by a link with no
 * explanation is how invitations get ignored.
 */
const route = useRoute()
const router = useRouter()
const session = useSessionStore()
const organizations = useOrganizationsStore()
const toast = useToast()

const token = computed(() => String(route.params.token ?? ''))
/** Where login and register send them back to - this page, to finish the job. */
const next = computed(() => `/invite/${token.value}`)

const preview = ref<InvitationPreview | null>(null)
const loading = ref(true)
const accepting = ref(false)
const problem = ref<string | null>(null)

/**
 * A link stops matching the moment anyone clicks Resend: only the token's hash is stored,
 * so a resend mints a new one and the link in the older email dies. The API cannot tell a
 * superseded token from a made-up one - it has nothing left to compare it against - so one
 * message has to cover both, and the useful half is where to find a working link.
 *
 * This is reached most often by someone who opened the invitation, went away to register,
 * and came back to a page whose token was rotated while they were gone.
 */
const deadLinkMessage =
  'This invitation link is no longer valid. It may have been replaced by a newer one, '
  + 'so open the most recent invitation email you received - or ask whoever invited you to send a new link.'

onMounted(async () => {
  // Both halves of the answer: is the link real, and is anyone signed in to use it.
  await session.load()

  try {
    preview.value = await previewInvitation(token.value)
  } catch (error) {
    problem.value =
      error instanceof ApiError && error.status === 404
        ? deadLinkMessage
        : (error as Error).message
  } finally {
    loading.value = false
  }
})

const statusMessage = computed(() => {
  switch (preview.value?.status) {
    case 'accepted':
      return 'This invitation has already been used.'
    case 'revoked':
      return 'This invitation was withdrawn.'
    case 'expired':
      return 'This invitation has expired. Ask whoever invited you to send a new one.'
    default:
      return null
  }
})

async function accept() {
  accepting.value = true
  try {
    const accepted = await acceptInvitation(token.value)
    // Land them *inside* the organization they just joined, not on whichever one the
    // browser last remembered.
    await organizations.reload()
    organizations.select(accepted.organizationSlug)
    toast.success(
      accepted.alreadyMember
        ? `You are already in ${accepted.organizationName}.`
        : `Welcome to ${accepted.organizationName}.`,
    )
    // A project invitation lands them on that project: a stakeholder of one project has no
    // reason to start anywhere else.
    await router.replace(
      accepted.projectKey ? `/o/${accepted.organizationSlug}/p/${accepted.projectKey}/items` : '/',
    )
  } catch (error) {
    // 409 means the link was revoked, expired or used while this page was open. Re-read
    // rather than guess: the preview then says which.
    toast.error(error)
    try {
      preview.value = await previewInvitation(token.value)
    } catch {
      problem.value = deadLinkMessage
    }
  } finally {
    accepting.value = false
  }
}
</script>

<template>
  <UiPageState v-if="loading" state="loading" />

  <AuthCard
    v-else-if="problem || !preview"
    title="Invitation link not valid"
    :description="problem ?? deadLinkMessage"
  >
    <Button class="w-full" @click="router.replace('/')">Go to Aictiq</Button>
  </AuthCard>

  <AuthCard
    v-else-if="statusMessage"
    :title="preview.organizationName"
    :description="statusMessage"
  >
    <Button class="w-full" @click="router.replace('/')">Go to Aictiq</Button>
  </AuthCard>

  <AuthCard
    v-else
    :title="`Join ${preview.organizationName}`"
    :description="`${preview.invitedByName} invited you as ${preview.role === 'admin' ? 'an' : 'a'} ${preview.role}.`"
  >
    <div class="space-y-4">
      <p class="text-muted-foreground text-center text-xs">
        Sent to {{ preview.maskedEmail }} · expires
        {{ new Date(preview.expiresAt).toLocaleDateString() }}
      </p>

      <template v-if="session.isAuthenticated">
        <Button class="w-full" :disabled="accepting" @click="accept">
          <Loader2 v-if="accepting" class="animate-spin" aria-hidden="true" />
          Accept invitation
        </Button>
        <p class="text-muted-foreground text-center text-xs">
          You are signed in as {{ session.user?.email }}. Accepting joins this account.
        </p>
      </template>

      <template v-else>
        <!-- `next` brings them straight back here, so the link survives the detour. -->
        <Button as-child class="w-full">
          <RouterLink :to="{ name: 'register', query: { next } }">
            Create an account to join
          </RouterLink>
        </Button>
        <Button as-child variant="outline" class="w-full">
          <RouterLink :to="{ name: 'login', query: { next } }">
            I already have an account
          </RouterLink>
        </Button>
      </template>
    </div>
  </AuthCard>
</template>
