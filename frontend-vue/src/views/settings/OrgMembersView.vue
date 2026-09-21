<script setup lang="ts">
import { Loader2, MoreHorizontal, Search } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import {
  canAssignRole,
  canRemoveMember,
  canSetFactoryOperator,
  factoryFlagIsChoosable,
  listMembers,
  orgRoles,
  removeMember,
  setMemberFactoryOperator,
  setMemberRole,
  type Member,
} from '@/api/members'
import { hasOrgRole, type OrgRole } from '@/api/organizations'
import EmptyState from '@/components/common/EmptyState.vue'
import RoleSelect from '@/components/common/RoleSelect.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { useOrgScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { orgSettingsPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

/**
 * Who is in this organization, and what they may do here. Agents are in this roster too —
 * they are ordinary members that sign in with a token — which is why every row carries the
 * avatar's bot badge rather than relying on the name.
 *
 * Every control is disabled by the same rule the API enforces, so nothing on the page
 * offers an action that would come back 403 — but the server is still the authority, and
 * a refusal that arrives anyway is shown rather than swallowed.
 */
const org = useOrgScope()
const organizations = useOrganizationsStore()
const session = useSessionStore()
const router = useRouter()
const toast = useToast()

const search = ref('')
const page = ref(1)
const pageSize = 20

const members = ref<Member[]>([])
const totalCount = ref(0)
const loading = ref(true)
const busyUserId = ref<string | null>(null)

const slug = computed(() => org.slug.value)
/** What *I* am here — the input to every permission decision on this page. */
const myRole = computed<OrgRole | undefined>(() => org.record.value?.role)
/** Only admins and owners may invite, and the invitations tab refuses everyone else. */
const mayInvite = computed(() => hasOrgRole(myRole.value, 'admin'))
const totalPages = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))

async function load() {
  loading.value = true
  try {
    const result = await listMembers(slug.value, {
      page: page.value,
      pageSize,
      search: search.value.trim() || undefined,
    })
    members.value = result.items
    totalCount.value = result.totalCount
  } catch (error) {
    toast.error(error)
  } finally {
    loading.value = false
  }
}

watch([slug, page], load, { immediate: true })

// Typing is not a page change, and a filter that keeps you on page 4 of the old result
// shows an empty list for no visible reason.
let debounce: ReturnType<typeof setTimeout> | undefined
watch(search, () => {
  clearTimeout(debounce)
  debounce = setTimeout(() => {
    // Changing the page is already watched, so only reload here when it did not change.
    if (page.value !== 1) page.value = 1
    else void load()
  }, 250)
})

function isMe(member: Member) {
  return member.userId === session.user?.id
}

function mayAssign(member: Member, desired: OrgRole) {
  return myRole.value !== undefined && canAssignRole(myRole.value, member.role, desired)
}

function mayRemove(member: Member) {
  // Leaving is always yours to do; the last owner is stopped by the API, not by the menu.
  return isMe(member) || (myRole.value !== undefined && canRemoveMember(myRole.value, member.role))
}

async function changeRole(member: Member, role: OrgRole) {
  if (role === member.role) return

  busyUserId.value = member.userId
  try {
    const updated = await setMemberRole(slug.value, member.userId, role)
    member.role = updated.role
    // Demoting to guest clears it, so the row follows what the server now says.
    member.canOperateFactory = updated.canOperateFactory
    // Demoting yourself changes what the rest of the app offers you, this page included.
    if (isMe(member)) await org.reload()
    toast.success(`${member.displayName} is now ${role}.`)
  } catch (error) {
    // The server's own title is the message — including the 409 the page cannot predict,
    // where the database refused to leave the organization without an owner. Whatever the
    // row now says is a guess, so it is re-read rather than left as the click drew it.
    toast.error(error)
    await load()
  } finally {
    busyUserId.value = null
  }
}

/**
 * A member who may not start AI work is a stakeholder: typically a client following a
 * project. Owners and admins always operate the factory and guests never do, so only a
 * member's row offers the choice.
 */
function mayChangeFactory(member: Member) {
  return myRole.value !== undefined && canSetFactoryOperator(myRole.value, member.role)
}

async function toggleFactory(member: Member, canOperateFactory: boolean) {
  busyUserId.value = member.userId
  try {
    const updated = await setMemberFactoryOperator(slug.value, member.userId, canOperateFactory)
    member.canOperateFactory = updated.canOperateFactory
    if (isMe(member)) await org.reload()
    toast.success(
      canOperateFactory
        ? `${member.displayName} can start AI work.`
        : `${member.displayName} can no longer start AI work.`,
    )
  } catch (error) {
    toast.error(error)
    await load()
  } finally {
    busyUserId.value = null
  }
}

async function remove(member: Member) {
  busyUserId.value = member.userId
  try {
    await removeMember(slug.value, member.userId)
    if (isMe(member)) {
      // We just left: this page answers 404 from here on, so the store drops the
      // organization and we go somewhere that still exists for us.
      organizations.remove(slug.value)
      toast.success('You left the organization.')
      await router.replace('/')
      return
    }
    toast.success(`${member.displayName} was removed.`)
    await load()
  } catch (error) {
    toast.error(error)
    await load()
  } finally {
    busyUserId.value = null
  }
}
</script>

<template>
  <SettingsSection wide>
    <header class="flex items-center justify-between gap-3 pb-3">
      <div>
        <h2 class="text-sm font-medium">People</h2>
        <p class="text-muted-foreground mt-0.5 text-xs">
          Everyone in this organization, agents included, and what they can do here.
        </p>
      </div>
      <div class="flex items-center gap-3">
        <span class="text-muted-foreground text-xs">{{ totalCount }} total</span>
        <!-- The invite flow lives on its own tab now: sending one is the start of a list
             that has to be managed afterwards, not a one-off dialog. -->
        <Button
          v-if="mayInvite"
          size="sm"
          @click="router.push(orgSettingsPath(slug, 'invitations'))"
        >
          Invite people
        </Button>
      </div>
    </header>

    <div class="relative pb-3">
      <Search
        class="text-muted-foreground pointer-events-none absolute top-4 left-2.5 size-3.5 -translate-y-1/2"
        aria-hidden="true"
      />
      <label for="members-search" class="sr-only">Filter members</label>
      <Input
        id="members-search"
        v-model="search"
        class="pl-8"
        placeholder="Filter by name or email…"
        autocomplete="off"
      />
    </div>

    <UiPageState v-if="loading" state="loading" />

    <EmptyState
      v-else-if="members.length === 0"
      :title="search ? 'Nobody matches that' : 'Nobody here yet'"
      :description="
        search
          ? 'Try a different name or email address.'
          : 'Invite people by email, or share the link the invitation gives you.'
      "
      icon="◍"
    >
      <Button v-if="search" variant="secondary" @click="search = ''">Clear search</Button>
      <Button v-else-if="mayInvite" @click="router.push(orgSettingsPath(slug, 'invitations'))">
        Invite people
      </Button>
    </EmptyState>

    <div v-else class="w-full overflow-x-auto">
      <table class="w-full min-w-[38rem] border-collapse text-sm sm:min-w-0">
        <thead>
          <tr class="border-border border-b">
            <th scope="col" class="font-label px-3 py-2 text-left">Member</th>
            <th scope="col" class="font-label px-3 py-2 text-left">Role</th>
            <th scope="col" class="font-label px-3 py-2 text-left">
              <span title="Whether they may start, cancel and watch AI runs">AI work</span>
            </th>
            <th scope="col" class="font-label px-3 py-2 text-left">Joined</th>
            <th scope="col" class="w-10 px-3 py-2"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="member in members"
            :key="member.userId"
            class="border-border/60 hover:bg-accent/60 border-b last:border-b-0"
          >
            <td class="px-3 py-2">
              <div class="flex items-center gap-2.5">
                <UserAvatar :name="member.displayName" :is-agent="member.isAgent" />
                <div class="min-w-0">
                  <div class="truncate font-medium">
                    {{ member.displayName }}
                    <span v-if="isMe(member)" class="text-muted-foreground font-normal">(you)</span>
                  </div>
                  <div v-if="member.email" class="text-muted-foreground truncate text-xs">
                    {{ member.email }}
                  </div>
                </div>
              </div>
            </td>
            <td class="px-3 py-2">
              <!-- No disabled state of its own: RoleSelect locks itself when every other
                 role is out of reach, which is exactly the row nobody may touch. -->
              <RoleSelect
                :model-value="member.role"
                :roles="orgRoles"
                :can-assign="(role) => mayAssign(member, role as OrgRole)"
                :disabled="busyUserId === member.userId"
                :label="`Role for ${member.displayName}`"
                @update:model-value="(role) => changeRole(member, role as OrgRole)"
              />
            </td>
            <td class="px-3 py-2 text-xs">
              <label
                v-if="factoryFlagIsChoosable(member.role)"
                class="inline-flex items-center gap-1.5"
                :class="mayChangeFactory(member) ? 'cursor-pointer' : 'text-muted-foreground'"
              >
                <input
                  type="checkbox"
                  :checked="member.canOperateFactory"
                  :disabled="!mayChangeFactory(member) || busyUserId === member.userId"
                  :aria-label="`${member.displayName} may start AI work`"
                  @change="
                    toggleFactory(member, ($event.target as HTMLInputElement).checked)
                  "
                />
                {{ member.canOperateFactory ? 'Allowed' : 'Stakeholder' }}
              </label>
              <span
                v-else
                class="text-muted-foreground"
                :title="
                  member.role === 'guest'
                    ? 'Guests never start AI work.'
                    : 'Owners and admins always may.'
                "
              >
                {{ member.canOperateFactory ? 'Always' : 'Never' }}
              </span>
            </td>
            <td class="text-muted-foreground px-3 py-2 text-xs">
              {{ new Date(member.joinedAt).toLocaleDateString() }}
            </td>
            <td class="px-3 py-2 text-right">
              <Loader2
                v-if="busyUserId === member.userId"
                class="text-muted-foreground inline size-4 animate-spin"
                aria-hidden="true"
              />
              <DropdownMenu v-else-if="mayRemove(member)">
                <DropdownMenuTrigger as-child>
                  <Button variant="ghost" size="icon" :aria-label="`Manage ${member.displayName}`">
                    <MoreHorizontal class="size-4" aria-hidden="true" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end" class="w-56">
                  <DropdownMenuItem variant="destructive" @select="remove(member)">
                    {{ isMe(member) ? 'Leave organization' : 'Remove from organization' }}
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="totalPages > 1" class="flex items-center justify-end gap-2 pt-3">
      <span class="text-muted-foreground text-xs">Page {{ page }} of {{ totalPages }}</span>
      <Button variant="outline" size="sm" :disabled="page <= 1" @click="page -= 1">
        Previous
      </Button>
      <Button variant="outline" size="sm" :disabled="page >= totalPages" @click="page += 1">
        Next
      </Button>
    </div>
  </SettingsSection>
</template>
