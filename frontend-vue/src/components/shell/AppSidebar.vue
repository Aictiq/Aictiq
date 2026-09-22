<script setup lang="ts">
import {
  Bot,
  CalendarRange,
  ChevronsLeft,
  CirclePlay,
  Factory,
  FolderOpen,
  Inbox,
  LayoutDashboard,
  ListChecks,
  ListTodo,
  LogOut,
  NotebookText,
  Play,
  Settings,
  SquareKanban,
  UserRound,
  Users,
} from '@lucide/vue'
import { computed, onMounted } from 'vue'
import { RouterLink, useRouter } from 'vue-router'

import ProjectBadge from '@/components/common/ProjectBadge.vue'
import UserAvatar from '@/components/common/UserAvatar.vue'
import OrgSwitcher from '@/components/shell/OrgSwitcher.vue'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { avatarUrl } from '@/api/profile'
import { useLogout } from '@/composables/useLogout'
import {
  factoryPath,
  orgSettingsPath,
  projectDashboardPath,
  projectItemsPath,
  wikiPath,
} from '@/router/paths'
import { useOnboardingStore } from '@/stores/onboarding'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'
import { useTeamsStore } from '@/stores/teams'
import { useUiStore } from '@/stores/ui'

/**
 * The left rail. Three sections, matching the design: what is mine, what is in this
 * project, what is in the organization.
 *
 * The "Project" links have no project in their URLs - they act on the *selected* one,
 * which is what the project list above them chooses. That is the same shape as the
 * organization switcher, and for the same reason: a person works in one place at a time,
 * and every request names it anyway.
 */
const ui = useUiStore()
const session = useSessionStore()
const organizations = useOrganizationsStore()
const onboarding = useOnboardingStore()
const router = useRouter()
const logout = useLogout()

const avatar = computed(() =>
  session.user ? avatarUrl(session.user.id, session.user.avatarKey) : null,
)
const projects = useProjectsStore()
const teams = useTeamsStore()

const props = withDefaults(defineProps<{ mobile?: boolean }>(), { mobile: false })
const collapsed = computed(() => !props.mobile && ui.sidebarCollapsed)

onMounted(async () => {
  await projects.load()
  // The team list depends on which project is selected, so it follows rather than races.
  await teams.load()
})

/** Archived projects stay out of the rail; the projects page is where they are found. */
const visibleProjects = computed(() => projects.projects.filter((p) => !p.isArchived))

/** One rail link. `tour` names the product-tour anchor the link carries. */
interface NavItem {
  label: string
  icon: typeof ListTodo
  to: string
  shortcut?: string
  count?: number
  tour?: string
}

const workspace: NavItem[] = [
  { label: 'My work', icon: ListTodo, to: '/', shortcut: 'g then m' },
  { label: 'Inbox', icon: Inbox, to: '/inbox', count: 0 },
]

// Project screens name their scope in the URL. That makes a browser reload and a pasted
// link show the same project as the rail, rather than relying on a remembered selection.
const project = computed<NavItem[]>(() => {
  const slug = organizations.currentSlug
  const projectKey = projects.currentKey
  const hasProject = Boolean(slug && projectKey)

  return [
    {
      label: 'Overview',
      icon: LayoutDashboard,
      to: hasProject ? projectDashboardPath(slug!, projectKey!) : '/',
    },
    {
      label: 'Items',
      icon: ListTodo,
      to: hasProject ? projectItemsPath(slug!, projectKey!) : '/items',
      shortcut: 'g then i',
      tour: 'items-link',
    },
    {
      label: 'Wiki',
      icon: NotebookText,
      to: hasProject ? wikiPath(slug!, projectKey!) : '/',
      tour: 'wiki-link',
    },
  ]
})

/** Selecting a project must also move the content pane into that project's work. */
async function selectProject(projectKey: string) {
  projects.select(projectKey)
  const slug = organizations.currentSlug
  if (slug) await router.push(projectItemsPath(slug, projectKey))
  closeMobileNavigation()
}

function closeMobileNavigation() {
  if (props.mobile) ui.setMobileSidebarOpen(false)
}

function openAccountSettings() {
  closeMobileNavigation()
  void router.push('/settings/profile')
}

// A backlog, a board and a sprint belong to a *team*, not to a project - which is what
// teams are for. These act on the selected team the same way the project links act on the
// selected project.
const teamNav = computed<NavItem[]>(() => {
  const slug = organizations.currentSlug
  const projectKey = projects.currentKey
  const teamId = teams.currentId
  const base = slug && projectKey && teamId ? `/o/${slug}/p/${projectKey}/teams/${teamId}` : ''
  return [
    { label: 'Backlog', icon: ListTodo, to: base ? `${base}/backlog` : '/backlog' },
    { label: 'Board', icon: SquareKanban, to: '/board', shortcut: 'g then b' },
    { label: 'Sprints', icon: CalendarRange, to: base ? `${base}/sprints` : '/sprints' },
  ]
})

/**
 * A section whose links act on a selection is hidden until there is one - links that
 * silently do nothing are worse than links that are not there.
 */
function sectionVisible(items: unknown[]) {
  if (items === project.value) return Boolean(projects.current)
  if (items === teamNav.value) return Boolean(teams.current)
  return true
}

/**
 * These name the organization in their URLs rather than acting on the selected one, unlike
 * the project and team links above. Settings are what people paste to each other - "look at
 * the role you gave me" has to open in the organization it was written about, not in
 * whichever one the reader happens to have selected.
 */
const organization = computed<NavItem[]>(() => {
  const slug = organizations.currentSlug
  // Hidden from stakeholders and guests: a link to a place they cannot work is noise.
  const factory = organizations.current?.canOperateFactory
    ? [
        {
          label: 'Factory',
          icon: Factory,
          to: slug ? factoryPath(slug) : '/factory',
          shortcut: 'g then f',
          tour: 'factory-link',
        },
      ]
    : []
  return [
    ...factory,
    { label: 'Agents', icon: Bot, to: '/agents' },
    { label: 'Members', icon: UserRound, to: slug ? orgSettingsPath(slug, 'members') : '/members' },
    {
      label: 'Settings',
      icon: Settings,
      to: slug ? orgSettingsPath(slug) : '/settings/organization',
    },
  ]
})
</script>

<template>
  <aside
    :id="mobile ? 'mobile-navigation' : undefined"
    class="bg-sidebar border-sidebar-border flex h-full flex-none flex-col border-r transition-[width] duration-150"
    :style="{
      width: mobile
        ? '100%'
        : collapsed
          ? 'var(--spacing-sidebar-collapsed)'
          : 'var(--spacing-sidebar)',
    }"
    aria-label="Primary"
  >
    <OrgSwitcher :collapsed="collapsed" :mobile="mobile" />

    <nav data-tour="sidebar-nav" class="flex-1 overflow-y-auto p-2">
      <template
        v-for="section in [
          { label: 'Workspace', items: workspace },
          { label: projects.current?.name ?? 'Project', items: project },
          { label: teams.current?.name ?? 'Team', items: teamNav },
          { label: 'Organization', items: organization },
        ]"
        :key="section.label"
      >
        <template v-if="section.items === project">
          <div data-tour="project-list">
            <div v-if="!collapsed" class="font-label px-2 pt-3 pb-1">Projects</div>
            <button
              v-for="entry in visibleProjects"
              :key="entry.id"
              type="button"
              class="text-sidebar-foreground/80 hover:bg-sidebar-accent hover:text-sidebar-foreground flex min-h-11 w-full items-center gap-2 rounded px-2 py-2 text-sm md:min-h-0 md:px-1.5 md:py-1 md:text-[12.5px]"
              :class="
                entry.key === projects.currentKey &&
                'bg-sidebar-accent text-sidebar-foreground font-medium'
              "
              :title="collapsed ? entry.name : undefined"
              @click="selectProject(entry.key)"
            >
              <ProjectBadge :project="entry" />
              <span v-if="!collapsed" class="flex-1 truncate text-left">
                {{ entry.name }}
              </span>
            </button>
            <RouterLink
              to="/projects"
              class="text-sidebar-foreground/80 hover:bg-sidebar-accent hover:text-sidebar-foreground flex min-h-11 items-center gap-2 rounded px-2 py-2 text-sm md:min-h-0 md:px-1.5 md:py-1 md:text-[12.5px]"
              active-class="bg-sidebar-accent text-sidebar-foreground font-medium"
              :title="collapsed ? 'All projects' : undefined"
              @click="closeMobileNavigation"
            >
              <FolderOpen class="size-3.5 flex-none opacity-85" aria-hidden="true" />
              <span v-if="!collapsed" class="flex-1 truncate text-left">
                {{ visibleProjects.length === 0 ? 'New project' : 'All projects' }}
              </span>
            </RouterLink>
          </div>
        </template>

        <template v-if="section.items === teamNav && projects.current && teams.teams.length > 1">
          <div v-if="!collapsed" class="font-label px-2 pt-3 pb-1">Teams</div>
          <button
            v-for="entry in teams.teams"
            :key="entry.id"
            type="button"
            class="text-sidebar-foreground/80 hover:bg-sidebar-accent hover:text-sidebar-foreground flex min-h-11 w-full items-center gap-2 rounded px-2 py-2 text-sm md:min-h-0 md:px-1.5 md:py-1 md:text-[12.5px]"
            :class="
              entry.id === teams.currentId &&
              'bg-sidebar-accent text-sidebar-foreground font-medium'
            "
            :title="collapsed ? entry.name : undefined"
            @click="teams.select(entry.id)"
          >
            <Users class="size-3.5 flex-none opacity-85" aria-hidden="true" />
            <span v-if="!collapsed" class="flex-1 truncate text-left">
              {{ entry.name }}
            </span>
          </button>
        </template>

        <div
          v-if="!collapsed && sectionVisible(section.items)"
          class="font-label px-2 pt-3 pb-1 first:pt-1.5"
        >
          {{ section.label }}
        </div>
        <RouterLink
          v-for="item in sectionVisible(section.items) ? section.items : []"
          :key="item.label"
          :to="item.to"
          :data-tour="item.tour"
          class="text-sidebar-foreground/80 hover:bg-sidebar-accent hover:text-sidebar-foreground flex min-h-11 items-center gap-2 rounded px-2 py-2 text-sm md:min-h-0 md:px-1.5 md:py-1 md:text-[12.5px]"
          active-class="bg-sidebar-accent text-sidebar-foreground font-medium"
          :title="collapsed ? item.label : undefined"
          @click="closeMobileNavigation"
        >
          <component :is="item.icon" class="size-3.5 flex-none opacity-85" aria-hidden="true" />
          <span v-if="!collapsed" class="flex-1 truncate text-left">{{ item.label }}</span>
          <span
            v-if="!collapsed && 'count' in item && item.count !== undefined"
            class="text-muted-foreground font-mono text-[10px]"
            >{{ item.count }}</span
          >
        </RouterLink>
      </template>
    </nav>

    <div class="border-sidebar-border flex items-center gap-2 border-t p-2">
      <!-- The whole block is the way to one's own account: a person looking for their
           profile - or for the way out - looks at their own name, not at a gear icon
           shared with the org. Signing out lives here rather than only in the palette,
           because someone who wants to leave should not have to know a shortcut. -->
      <DropdownMenu>
        <DropdownMenuTrigger
          data-tour="account-menu"
          class="hover:bg-sidebar-accent focus-visible:ring-ring -m-1 flex min-w-0 flex-1 items-center gap-2 rounded p-1 text-left focus-visible:ring-2 focus-visible:outline-none"
          :aria-label="`Account: ${session.user?.fullName ?? 'signed out'}`"
        >
          <UserAvatar
            :name="session.user?.fullName ?? 'Signed out'"
            :is-agent="session.user?.isAgent ?? false"
            :src="avatar"
          />
          <div v-if="!collapsed" class="min-w-0 flex-1">
            <div class="truncate text-xs font-medium">{{ session.user?.fullName }}</div>
            <div class="text-muted-foreground truncate text-[10.5px]">
              {{ session.user?.email }}
            </div>
          </div>
        </DropdownMenuTrigger>

        <DropdownMenuContent align="start" side="top" class="w-56">
          <DropdownMenuLabel class="min-w-0">
            <div class="truncate text-xs font-medium">{{ session.user?.fullName }}</div>
            <div class="text-muted-foreground truncate text-[10.5px] font-normal">
              {{ session.user?.email }}
            </div>
          </DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem @select="openAccountSettings">
            <Settings class="size-3.5" aria-hidden="true" />
            Account settings
          </DropdownMenuItem>
          <DropdownMenuItem @select="onboarding.openChecklist()">
            <ListChecks class="size-3.5" aria-hidden="true" />
            Get started
          </DropdownMenuItem>
          <DropdownMenuItem
            v-if="onboarding.resumeEligible"
            @select="onboarding.startTour(onboarding.lastStepId)"
          >
            <Play class="size-3.5" aria-hidden="true" />
            Resume tour
          </DropdownMenuItem>
          <DropdownMenuItem @select="onboarding.startTour()">
            <CirclePlay class="size-3.5" aria-hidden="true" />
            Replay product tour
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem @select="logout()">
            <LogOut class="size-3.5" aria-hidden="true" />
            Log out
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
      <button
        v-if="!mobile"
        type="button"
        class="text-muted-foreground hover:text-foreground rounded p-1"
        :aria-label="collapsed ? 'Expand sidebar' : 'Collapse sidebar'"
        :aria-expanded="!collapsed"
        @click="ui.toggleSidebar()"
      >
        <ChevronsLeft class="size-3.5 transition-transform" :class="collapsed && 'rotate-180'" />
      </button>
    </div>
  </aside>
</template>
