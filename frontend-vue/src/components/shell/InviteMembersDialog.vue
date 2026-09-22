<script setup lang="ts">
import { Check, Copy, Loader2, TriangleAlert } from '@lucide/vue'
import { computed, ref, watch } from 'vue'

import {
  createInvitation,
  invitableRoles,
  parseAddresses,
  stakeholderInvitation,
  type InvitableRole,
  type InvitationLink,
} from '@/api/invitations'
import { listProjects, type Project } from '@/api/projects'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { useToast } from '@/composables/useToast'

/**
 * Inviting people, one paste at a time.
 *
 * The dialog stays open after sending, because the links are the result. On an instance
 * with no SMTP relay nothing was delivered and the link *is* the invitation - so it is
 * shown, copyable, rather than hidden behind a "sent!" that would not be true.
 *
 * Each address is its own request: the API takes one at a time, and one bad address in a
 * paste of twenty should not cost the other nineteen.
 *
 * Two kinds of invitation. *Team* is a colleague: any role, and a member may start
 * AI work unless the box is cleared. *Stakeholder* is someone outside the team, typically a
 * client, who follows one project: a member of that project who can see the board, add
 * items and comment, and may not start AI work. It is a preset over the same request, so an
 * admin never has to know that a flag exists to get it right.
 */
const open = defineModel<boolean>('open', { required: true })

const props = defineProps<{ slug: string }>()
const emit = defineEmits<{ invited: [] }>()

const toast = useToast()

type InviteKind = 'team' | 'stakeholder'

const input = ref('')
const kind = ref<InviteKind>('team')
const role = ref<InvitableRole>('member')
const canOperateFactory = ref(true)
const projectId = ref('')
const projects = ref<Project[]>([])
const sending = ref(false)
const results = ref<InvitationLink[]>([])
const failures = ref<{ email: string; message: string }[]>([])
const copied = ref<string | null>(null)

const addresses = computed(() => parseAddresses(input.value))

/** Archived projects are read-only; inviting someone to follow one is refused by the API. */
const invitableProjects = computed(() => projects.value.filter((p) => !p.isArchived))

/** A stakeholder is always invited to a project: that is the whole of what they are here for. */
const ready = computed(
  () => addresses.value.length > 0 && (kind.value === 'team' || projectId.value !== ''),
)

watch(open, (isOpen) => {
  if (!isOpen) return
  input.value = ''
  kind.value = 'team'
  role.value = 'member'
  canOperateFactory.value = true
  projectId.value = ''
  results.value = []
  failures.value = []
  copied.value = null
  void loadProjects()
})

// Admins always operate the factory and guests never do, so the box follows the role for
// them and is only a choice for a member.
watch(role, (next) => {
  canOperateFactory.value = next !== 'guest'
})

async function loadProjects() {
  try {
    projects.value = await listProjects(props.slug)
  } catch {
    // The stakeholder picker is then empty and says so; the team invitation still works.
    projects.value = []
  }
}

function request(email: string) {
  if (kind.value === 'stakeholder') {
    const { role: stakeholderRole, ...options } = stakeholderInvitation(projectId.value)
    return createInvitation(props.slug, email, stakeholderRole, options)
  }
  // Only a member's flag is sent: for admins and guests the role decides, and sending the
  // opposite would be refused.
  return createInvitation(
    props.slug,
    email,
    role.value,
    role.value === 'member' ? { canOperateFactory: canOperateFactory.value } : {},
  )
}

async function submit() {
  if (!ready.value) return

  sending.value = true
  results.value = []
  failures.value = []

  for (const email of addresses.value) {
    try {
      results.value.push(await request(email))
    } catch (error) {
      // Named, not counted: "3 failed" in a paste of twenty is not something anyone can
      // act on. The API's own message says whether it was a typo or a duplicate.
      failures.value.push({ email, message: (error as Error).message })
    }
  }

  sending.value = false
  input.value = ''

  if (results.value.length > 0) {
    emit('invited')
    const delivered = results.value.every((r) => r.emailSent)
    toast.success(
      delivered
        ? `Invited ${results.value.length} ${results.value.length === 1 ? 'person' : 'people'}.`
        : 'Invitations created. Email is not configured here - share the links below.',
    )
  }
}

async function copy(link: InvitationLink) {
  try {
    await navigator.clipboard.writeText(link.acceptUrl)
    copied.value = link.invitation.id
    setTimeout(() => (copied.value = null), 1500)
  } catch {
    // Clipboard access can be refused outright; the link is on screen and selectable.
    toast.error(new Error('Could not copy - select the link and copy it manually.'))
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="sm:max-w-lg">
      <DialogHeader>
        <DialogTitle>Invite people</DialogTitle>
        <DialogDescription>
          Paste addresses separated by commas, spaces or new lines. Everyone gets the same
          role; you can change it once they are here.
        </DialogDescription>
      </DialogHeader>

      <form id="invite-people" class="space-y-4" novalidate @submit.prevent="submit">
        <div class="space-y-1.5">
          <label for="invite-emails" class="text-sm font-medium">Email addresses</label>
          <textarea
            id="invite-emails"
            v-model="input"
            rows="3"
            placeholder="ada@example.com, grace@example.com"
            class="border-border bg-background focus-visible:ring-ring w-full rounded-lg border px-2.5 py-2 text-sm focus-visible:ring-2 focus-visible:outline-none"
          ></textarea>
          <p class="text-muted-foreground text-xs">
            <template v-if="addresses.length">
              {{ addresses.length }}
              {{ addresses.length === 1 ? 'address' : 'addresses' }} recognised.
            </template>
            <template v-else>Nothing recognised yet.</template>
          </p>
        </div>

        <fieldset class="space-y-1.5">
          <legend class="text-sm font-medium">Inviting</legend>
          <div class="grid grid-cols-2 gap-2" role="radiogroup" aria-label="Kind of invitation">
            <label
              v-for="option in [
                { value: 'team', title: 'Team', hint: 'A colleague, at any role.' },
                {
                  value: 'stakeholder',
                  title: 'Stakeholder',
                  hint: 'Follows one project. Cannot start AI work.',
                },
              ]"
              :key="option.value"
              class="border-border has-[:checked]:border-primary has-[:checked]:bg-accent/60 flex cursor-pointer flex-col rounded-lg border px-2.5 py-2"
            >
              <span class="flex items-center gap-1.5 text-sm font-medium">
                <input v-model="kind" type="radio" name="invite-kind" :value="option.value" />
                {{ option.title }}
              </span>
              <span class="text-muted-foreground text-xs">{{ option.hint }}</span>
            </label>
          </div>
        </fieldset>

        <template v-if="kind === 'team'">
          <div class="space-y-1.5">
            <label for="invite-role" class="text-sm font-medium">Role</label>
            <select
              id="invite-role"
              v-model="role"
              class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm capitalize focus-visible:ring-2 focus-visible:outline-none"
            >
              <option v-for="option in invitableRoles" :key="option" :value="option">
                {{ option }}
              </option>
            </select>
            <p class="text-muted-foreground text-xs">
              Ownership is handed over from the members list - it is never sent to an address.
            </p>
          </div>

          <label
            class="flex items-start gap-2 text-sm"
            :class="role === 'member' ? 'cursor-pointer' : 'text-muted-foreground'"
          >
            <input
              v-model="canOperateFactory"
              type="checkbox"
              class="mt-0.5"
              :disabled="role !== 'member'"
            />
            <span>
              Can start AI work
              <span class="text-muted-foreground block text-xs">
                <template v-if="role === 'admin'">Admins always can.</template>
                <template v-else-if="role === 'guest'">Guests never can.</template>
                <template v-else>Start, cancel and read the logs of agent runs.</template>
              </span>
            </span>
          </label>
        </template>

        <div v-else class="space-y-1.5">
          <label for="invite-project" class="text-sm font-medium">Project</label>
          <select
            id="invite-project"
            v-model="projectId"
            class="border-border bg-background focus-visible:ring-ring h-8 w-full rounded-lg border px-2.5 text-sm focus-visible:ring-2 focus-visible:outline-none"
          >
            <option value="" disabled>Choose a project…</option>
            <option v-for="project in invitableProjects" :key="project.id" :value="project.id">
              {{ project.key }} · {{ project.name }}
            </option>
          </select>
          <p class="text-muted-foreground text-xs">
            They join as a member of this project: they see its board, add items and comment.
            They cannot start, cancel or read AI runs. Like any member, they also see projects
            visible to the whole organization, so keep those private if that matters.
          </p>
        </div>
      </form>

      <div v-if="failures.length" class="space-y-1">
        <p
          v-for="failure in failures"
          :key="failure.email"
          class="text-destructive flex items-start gap-1.5 text-xs"
        >
          <TriangleAlert class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span><span class="font-medium">{{ failure.email }}</span> - {{ failure.message }}</span>
        </p>
      </div>

      <div v-if="results.length" class="space-y-2">
        <h3 class="font-label text-xs">
          {{ results.some((r) => r.emailSent) ? 'Invitation links' : 'Share these links' }}
        </h3>
        <div
          v-for="link in results"
          :key="link.invitation.id"
          class="border-border flex items-center gap-2 rounded-lg border px-2.5 py-1.5"
        >
          <div class="min-w-0 flex-1">
            <div class="truncate text-xs font-medium">{{ link.invitation.email }}</div>
            <div class="text-muted-foreground truncate font-mono text-[11px]">
              {{ link.acceptUrl }}
            </div>
          </div>
          <Button
            variant="ghost"
            size="icon"
            :aria-label="`Copy the link for ${link.invitation.email}`"
            @click="copy(link)"
          >
            <Check v-if="copied === link.invitation.id" class="size-4" aria-hidden="true" />
            <Copy v-else class="size-4" aria-hidden="true" />
          </Button>
        </div>
        <p class="text-muted-foreground text-xs">
          Each link works once and expires in seven days. Resending replaces it.
        </p>
      </div>

      <DialogFooter>
        <Button type="button" variant="ghost" @click="open = false">
          {{ results.length ? 'Done' : 'Cancel' }}
        </Button>
        <Button type="submit" form="invite-people" :disabled="sending || !ready">
          <Loader2 v-if="sending" class="animate-spin" aria-hidden="true" />
          Send {{ addresses.length || '' }}
          {{ addresses.length === 1 ? 'invitation' : 'invitations' }}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
