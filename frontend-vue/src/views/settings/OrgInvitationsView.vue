<script setup lang="ts">
import { Check, Copy, Loader2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import {
  listInvitations,
  resendInvitation,
  revokeInvitation,
  type Invitation,
  type InvitationStatus,
} from '@/api/invitations'
import { hasOrgRole } from '@/api/organizations'
import EmptyState from '@/components/common/EmptyState.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import InviteMembersDialog from '@/components/shell/InviteMembersDialog.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'

/**
 * The invitations that have been sent and not yet answered.
 *
 * Aictiq keeps only the SHA-256 of each link, so there is nothing here to copy back out:
 * every "send the link again" mints a new token and retires the previous one, and the
 * page has to say so rather than let someone believe both still work.
 */
const org = useOrgScope()
const toast = useToast()

const invitations = ref<Invitation[]>([])
const loading = ref(true)
const inviting = ref(false)
const busyId = ref<string | null>(null)
const copiedId = ref<string | null>(null)

const slug = computed(() => org.slug.value)
/**
 * The pending list is a list of email addresses belonging to people who have not agreed to
 * anything, and the API answers 403 to everyone below admin - so the tab shows who owns
 * this rather than a request that is certain to be refused.
 */
const mayManage = computed(() => hasOrgRole(org.record.value?.role, 'admin'))

const statusTone: Record<InvitationStatus, string> = {
  pending: 'text-muted-foreground',
  accepted: 'text-success',
  revoked: 'text-muted-foreground line-through',
  expired: 'text-destructive',
}

async function load() {
  if (!mayManage.value) {
    invitations.value = []
    loading.value = false
    return
  }

  loading.value = true
  try {
    invitations.value = await listInvitations(slug.value)
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([slug, mayManage], load, { immediate: true })

// The dialog sends one request per address and keeps the links on screen afterwards, so
// the list behind it is only worth re-reading once it is out of the way.
watch(inviting, (open) => {
  if (!open) void load()
})

async function copyLink(url: string, id: string) {
  try {
    await navigator.clipboard.writeText(url)
    copiedId.value = id
    setTimeout(() => (copiedId.value = null), 1500)
  } catch {
    toast.error(new Error('Could not copy to the clipboard.'))
  }
}

async function resend(invitation: Invitation) {
  busyId.value = invitation.id
  try {
    const link = await resendInvitation(slug.value, invitation.id)
    await copyLink(link.acceptUrl, invitation.id)
    // Worth saying out loud: the previous link stops working, because only its hash was
    // ever stored and a resend has to mint a new one.
    toast.success(
      link.emailSent
        ? 'A new invitation was sent. The previous link no longer works.'
        : 'A new link was copied. The previous link no longer works.',
    )
    await load()
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}

async function revoke(invitation: Invitation) {
  busyId.value = invitation.id
  try {
    await revokeInvitation(slug.value, invitation.id)
    toast.success(`The invitation to ${invitation.email} was withdrawn.`)
    await load()
  } catch (error) {
    toast.error(error)
  } finally {
    busyId.value = null
  }
}
</script>

<template>
  <SettingsSection wide>
    <EmptyState
      v-if="!mayManage"
      title="Invitations"
      description="Admins and owners manage who is invited here."
      icon="◇"
    />

    <template v-else>
      <header class="flex items-center justify-between gap-3 pb-3">
        <div>
          <h2 class="text-sm font-medium">Invitations</h2>
          <p class="text-muted-foreground mt-0.5 text-xs">
            Each link works once and expires in seven days. Sending it again replaces it -
            only its hash is stored here, so the previous one cannot be produced twice.
          </p>
        </div>
        <Button size="sm" @click="inviting = true">Invite people</Button>
      </header>

      <InviteMembersDialog v-model:open="inviting" :slug="slug" />

      <UiPageState v-if="loading" state="loading" />

      <EmptyState
        v-else-if="invitations.length === 0"
        title="Nothing outstanding"
        description="Invitations you send appear here until they are accepted or withdrawn."
        icon="◇"
      >
        <Button @click="inviting = true">Invite people</Button>
      </EmptyState>

      <table v-else class="w-full border-collapse text-sm">
        <thead>
          <tr class="border-border border-b">
            <th scope="col" class="font-label px-3 py-2 text-left">Invited</th>
            <th scope="col" class="font-label px-3 py-2 text-left">Role</th>
            <th scope="col" class="font-label px-3 py-2 text-left">Status</th>
            <th scope="col" class="font-label px-3 py-2 text-left">Expires</th>
            <th scope="col" class="w-32 px-3 py-2"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="invitation in invitations"
            :key="invitation.id"
            class="border-border/60 hover:bg-accent/60 border-b last:border-b-0"
          >
            <td class="px-3 py-2">
              <div class="truncate font-medium">{{ invitation.email }}</div>
              <div class="text-muted-foreground truncate text-xs">
                by {{ invitation.invitedByName }} ·
                {{ new Date(invitation.createdAt).toLocaleDateString() }}
              </div>
            </td>
            <td class="px-3 py-2">
              <div class="capitalize">{{ invitation.role }}</div>
              <div
                v-if="invitation.projectKey || !invitation.canOperateFactory"
                class="text-muted-foreground text-xs"
              >
                <template v-if="invitation.projectKey">on {{ invitation.projectKey }}</template>
                <template v-if="invitation.projectKey && !invitation.canOperateFactory"> · </template>
                <template v-if="!invitation.canOperateFactory">no AI work</template>
              </div>
            </td>
            <td class="px-3 py-2 text-xs capitalize" :class="statusTone[invitation.status]">
              {{ invitation.status }}
            </td>
            <td class="text-muted-foreground px-3 py-2 text-xs">
              {{ new Date(invitation.expiresAt).toLocaleDateString() }}
            </td>
            <td class="px-3 py-2 text-right">
              <Loader2
                v-if="busyId === invitation.id"
                class="text-muted-foreground inline size-4 animate-spin"
                aria-hidden="true"
              />
              <div
                v-else-if="invitation.status === 'pending' || invitation.status === 'expired'"
                class="flex items-center justify-end gap-1"
              >
                <!-- Resend, not "copy link": the stored token is a hash, so there is no
                     existing link to copy - a fresh one has to be minted, and it is put
                     on the clipboard because an instance without SMTP has no other way
                     to deliver it. -->
                <Button
                  variant="ghost"
                  size="icon"
                  :aria-label="`Send a new link to ${invitation.email}`"
                  @click="resend(invitation)"
                >
                  <Check v-if="copiedId === invitation.id" class="size-4" aria-hidden="true" />
                  <Copy v-else class="size-4" aria-hidden="true" />
                </Button>
                <Button variant="ghost" size="sm" @click="revoke(invitation)">Revoke</Button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </SettingsSection>
</template>
