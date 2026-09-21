<script setup lang="ts">
import { Loader2, Trash2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import { listMembers } from '@/api/members'
import { avatarUrl } from '@/api/profile'
import {
  hasProjectRole,
  listProjectMembers,
  projectRoles,
  removeProjectMember,
  setProjectMemberRole,
  type ProjectMember,
  type ProjectRole,
} from '@/api/projects'
import EmptyState from '@/components/common/EmptyState.vue'
import RoleSelect from '@/components/common/RoleSelect.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import UserSelect, { type UserOption } from '@/components/common/UserSelect.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import UiPageState from '@/components/UiPageState.vue'
import { Button } from '@/components/ui/button'
import { useProjectScope } from '@/composables/useSettingsScope'
import { useToast } from '@/composables/useToast'
import { useSessionStore } from '@/stores/session'

/**
 * Who is on the project, and what they may do here.
 *
 * Two kinds of row share the table. An **explicit** membership was granted on this
 * project and can be changed or taken away; an **implicit** one is a consequence of the
 * organization — an owner or admin, or a project everyone in the organization can see.
 * There is nothing to remove in the second case, only a role to raise, and giving such a
 * person an explicit membership is exactly how that is done.
 */
const project = useProjectScope()
const session = useSessionStore()
const router = useRouter()
const toast = useToast()

const record = ref(project.record.value!)
watch(project.record, (loaded) => loaded && (record.value = loaded), { immediate: true })

const members = ref<ProjectMember[]>([])
const candidates = ref<UserOption[]>([])
const loading = ref(true)
const failed = ref(false)
const loadingCandidates = ref(false)
const busyUserId = ref<string | null>(null)

const isAdmin = computed(() => hasProjectRole(record.value.role, 'admin'))
const isArchived = computed(() => record.value.isArchived)
/** An archived project answers 409 to every write, so none of them are offered. */
const mayManage = computed(() => isAdmin.value && !isArchived.value)

/**
 * The whole organization is offered, because an implicit member is a legitimate pick:
 * naming them explicitly is what raises their role. Someone already named is kept in the
 * list but disabled — a picker that dropped them would read as "they are not here".
 */
const options = computed<UserOption[]>(() => {
  const named = new Set(members.value.filter((m) => !m.isImplicit).map((m) => m.userId))
  return candidates.value.map((candidate) =>
    named.has(candidate.userId)
      ? { ...candidate, disabled: true, hint: 'already a member' }
      : candidate,
  )
})

async function loadMembers() {
  loading.value = true
  failed.value = false
  try {
    members.value = await listProjectMembers(project.slug.value, project.projectKey.value)
  } catch (error) {
    failed.value = true
    toast.error(error)
  } finally {
    loading.value = false
  }
}

/** Only fetched for someone who can actually add: it is the organization's roster, not this page's. */
async function loadCandidates() {
  if (!mayManage.value || candidates.value.length > 0) return

  loadingCandidates.value = true
  try {
    const page = await listMembers(project.slug.value, { pageSize: 100 })
    candidates.value = page.items.map((member) => ({
      userId: member.userId,
      displayName: member.displayName,
      email: member.email,
      avatarKey: member.avatarKey,
      isAgent: member.isAgent,
    }))
  } catch (error) {
    toast.error(error)
  } finally {
    loadingCandidates.value = false
  }
}

watch(
  [project.slug, project.projectKey],
  async () => {
    await loadMembers()
    await loadCandidates()
  },
  { immediate: true },
)

async function setRole(userId: string, role: ProjectRole) {
  busyUserId.value = userId
  try {
    await setProjectMemberRole(project.slug.value, project.projectKey.value, userId, role)
    members.value = await listProjectMembers(project.slug.value, project.projectKey.value)
  } catch (error) {
    toast.error(error)
  } finally {
    busyUserId.value = null
  }
}

/** The picker is an action rather than a selection, so it never holds a value of its own. */
function add(value: string | string[] | null) {
  const userId = Array.isArray(value) ? value[0] : value
  // Member is the role someone joins on; raising it is a second, deliberate step.
  if (userId) void setRole(userId, 'member')
}

async function remove(member: ProjectMember) {
  busyUserId.value = member.userId
  try {
    await removeProjectMember(project.slug.value, project.projectKey.value, member.userId)
    if (member.userId === session.user?.id) {
      // They just left. A private project closes behind them.
      await router.replace('/projects')
      return
    }
    members.value = await listProjectMembers(project.slug.value, project.projectKey.value)
  } catch (error) {
    toast.error(error)
  } finally {
    busyUserId.value = null
  }
}

/**
 * An organization owner or admin is a project admin everywhere, whatever the visibility;
 * anyone else who is here without a membership is here because the project is open to the
 * organization.
 */
function implicitReason(member: ProjectMember): string {
  return member.role === 'admin'
    ? 'from their role in the organization'
    : 'because this project is visible to the organization'
}

function pictureOf(member: ProjectMember) {
  return avatarUrl(member.userId, member.avatarKey)
}
</script>

<template>
  <SettingsSection wide title="People" description="Who can open this project, and what they may do in it.">
    <div v-if="mayManage" class="max-w-sm pb-4">
      <UserSelect
        :model-value="null"
        :options="options"
        :loading="loadingCandidates"
        label="Add someone to this project"
        placeholder="Add someone from the organization…"
        empty-text="Nobody in this organization matches."
        @update:model-value="add"
      />
    </div>

    <p v-else-if="isAdmin && isArchived" class="text-muted-foreground pb-4 text-xs">
      This project is archived, so its roster is read-only. Restore it under General to change
      who is on it.
    </p>

    <UiPageState v-if="loading" state="loading" />

    <UiPageState
      v-else-if="failed"
      state="error"
      title="The roster could not be loaded"
      description="The request did not come back. Try again."
    >
      <Button variant="secondary" @click="loadMembers">Try again</Button>
    </UiPageState>

    <EmptyState
      v-else-if="members.length === 0"
      title="Nobody is on this project"
      description="A private project with no members can only be opened by the organization's owners and admins."
      icon="◍"
    />

    <table v-else class="w-full border-collapse text-sm">
      <thead>
        <tr class="border-border border-b">
          <th scope="col" class="font-label px-3 py-2 text-left">Person</th>
          <th scope="col" class="font-label px-3 py-2 text-left">Role</th>
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
              <UserAvatar
                :name="member.displayName"
                :is-agent="member.isAgent"
                :src="pictureOf(member)"
              />
              <div class="min-w-0">
                <div class="truncate font-medium">{{ member.displayName }}</div>
                <div v-if="member.email" class="text-muted-foreground truncate text-xs">
                  {{ member.email }}
                </div>
              </div>
            </div>
          </td>
          <td class="px-3 py-2">
            <RoleSelect
              :model-value="member.role"
              :roles="projectRoles"
              :disabled="!mayManage || busyUserId === member.userId"
              :label="`Role for ${member.displayName} on this project`"
              @update:model-value="(role) => setRole(member.userId, role as ProjectRole)"
            />
            <p v-if="member.isImplicit" class="text-muted-foreground mt-1 text-[11px]">
              Here {{ implicitReason(member) }}
            </p>
          </td>
          <td class="px-3 py-2 text-right">
            <Loader2
              v-if="busyUserId === member.userId"
              class="text-muted-foreground inline size-4 animate-spin"
              aria-hidden="true"
            />
            <Button
              v-else-if="mayManage && !member.isImplicit"
              variant="ghost"
              size="icon"
              :aria-label="
                member.userId === session.user?.id
                  ? 'Leave this project'
                  : `Remove ${member.displayName} from this project`
              "
              @click="remove(member)"
            >
              <Trash2 class="size-4" aria-hidden="true" />
            </Button>
          </td>
        </tr>
      </tbody>
    </table>

    <p v-if="!loading && !failed && members.length > 0" class="text-muted-foreground pt-3 text-xs">
      Someone here without a membership of their own — an owner or admin of the organization, or
      everyone at once while the project is organization-visible — cannot be removed from it.
      Adding them explicitly is how their role is raised.
    </p>
  </SettingsSection>
</template>
