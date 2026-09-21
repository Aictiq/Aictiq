<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { useMediaQuery } from '@vueuse/core'
import { computed, defineAsyncComponent, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { getSubscription } from '@/api/billing'
import { getLatestGitHubRelease, getReleaseMetadata, isNewerRelease } from '@/api/meta'
import ImageLightbox from '@/components/common/ImageLightbox.vue'
import ItemDetailDialog from '@/components/items/ItemDetailDialog.vue'
import AppHeader from '@/components/shell/AppHeader.vue'
import PaymentBanner from '@/components/shell/PaymentBanner.vue'
import AppSidebar from '@/components/shell/AppSidebar.vue'
import CommandPalette from '@/components/shell/CommandPalette.vue'
import CreateOrganizationDialog from '@/components/shell/CreateOrganizationDialog.vue'
import ShortcutHelp from '@/components/shell/ShortcutHelp.vue'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'
import { useCommands } from '@/composables/useCommands'
import { useLogout } from '@/composables/useLogout'
import { useOrganizationSwitch } from '@/composables/useOrganizationSwitch'
import { useShortcut } from '@/composables/useShortcuts'
import { orgSettingsPath, projectSettingsPath } from '@/router/paths'
import { useOnboardingStore } from '@/stores/onboarding'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useUiStore } from '@/stores/ui'

/**
 * The authenticated frame: sidebar, header, content, and the item dialog any list opens with
 * `useItemModal`. It also owns the shell-level commands and shortcuts,
 * so every page inherits them without registering anything.
 */
const ui = useUiStore()
const organizations = useOrganizationsStore()
const projects = useProjectsStore()
const onboarding = useOnboardingStore()

// The tour and its checklist are dead weight for everyone who has already been through
// them, so neither chunk is fetched until the store says one is wanted — the welcome, a
// resumable tour, or an explicit Get started / replay. The store owns that visibility, so
// the gate can be a plain flag here.
const ProductTour = defineAsyncComponent(
  () => import('@/components/onboarding/ProductTour.vue'),
)
const GettingStartedPanel = defineAsyncComponent(
  () => import('@/components/onboarding/GettingStartedPanel.vue'),
)
const tourWanted = computed(
  () => onboarding.welcomeOpen || onboarding.tourActive || onboarding.resumeEligible,
)
const router = useRouter()
const route = useRoute()
const logout = useLogout()
const isMobile = useMediaQuery('(max-width: 767px)')

const creatingOrganization = ref(false)
// Creating one selects it; leaving the page of the organization being left is the
// other half of that (see useOrganizationSwitch).
const enterOrganization = useOrganizationSwitch()

watch(
  () => route.fullPath,
  () => ui.setMobileSidebarOpen(false),
)
watch(isMobile, (mobile) => {
  if (!mobile) ui.setMobileSidebarOpen(false)
})

function toggleNavigation() {
  if (isMobile.value) ui.toggleMobileSidebar()
  else ui.toggleSidebar()
}

// The shell is only ever mounted behind the auth guard, so this is the first moment the
// organization list can be asked for. `load()` is single-flight — a page that also needs
// it does not pay for a second round trip.
onMounted(() => void organizations.load())

// The welcome dialog is offered only once the preference record is known, so it never
// opens over a tour this person already finished in another tab.
onMounted(() => void onboarding.bootstrap())

// Every input to "may the welcome open now" is asynchronous and lands in its own order:
// the preference read, the organization list, the project list, and leaving the mandatory
// setup route. Watching them all is what makes the offer arrive whichever settles last —
// checking once after the read alone loses the race on a first login, where the record is
// back long before the first project list is.
watch(
  () => [
    onboarding.loaded,
    organizations.isResolved,
    projects.isResolved,
    onboarding.checklistOpen,
    route.fullPath,
  ] as const,
  () => onboarding.maybeShowWelcome(),
  { immediate: true },
)

// The payment-failed banner. One small request per organization, cached; a
// self-hosted instance answers it without touching the database and the banner stays
// empty. Failures are deliberately silent — the banner is a warning, not a page.
const billingSlug = computed(() => organizations.currentSlug)
const subscription = useQuery({
  queryKey: computed(() => ['billing-subscription', billingSlug.value]),
  queryFn: () => getSubscription(billingSlug.value!),
  enabled: computed(() => billingSlug.value !== null),
  staleTime: 5 * 60_000,
  retry: false,
})

// The server has to explicitly opt in before a browser contacts GitHub. Failed checks
// are silent: an unavailable update service must never make the application frame noisy.
const releaseMetadata = useQuery({
  queryKey: ['release-metadata'],
  queryFn: getReleaseMetadata,
  staleTime: Infinity,
  retry: false,
})
const latestRelease = useQuery({
  queryKey: computed(() => ['release-update', releaseMetadata.data.value?.updateCheck.repository]),
  queryFn: () => getLatestGitHubRelease(releaseMetadata.data.value!.updateCheck.repository!),
  enabled: computed(
    () =>
      releaseMetadata.data.value?.updateCheck.enabled === true &&
      releaseMetadata.data.value.updateCheck.repository !== null,
  ),
  staleTime: 24 * 60 * 60_000,
  retry: false,
})
const updateAvailable = computed(() => {
  const current = releaseMetadata.data.value?.version
  const latest = latestRelease.data.value?.version
  return current !== undefined && latest !== undefined && isNewerRelease(current, latest)
})

useCommands(() => [
  {
    id: 'nav.my-work',
    group: 'Navigation',
    label: 'Go to My work',
    icon: '↗',
    shortcut: 'g then m',
    run: () => void router.push('/'),
  },
  {
    id: 'nav.items',
    group: 'Navigation',
    label: 'Go to Items',
    icon: '↗',
    shortcut: 'g then i',
    run: () => void router.push('/items'),
  },
  {
    id: 'nav.board',
    group: 'Navigation',
    label: 'Go to Board',
    icon: '↗',
    shortcut: 'g then b',
    run: () => void router.push('/board'),
  },
  // Only for those who may operate it: a stakeholder is not offered the factory.
  ...(organizations.current?.canOperateFactory
    ? [
        {
          id: 'nav.factory',
          group: 'Navigation',
          label: 'Go to Factory',
          icon: '↗',
          shortcut: 'g then f',
          keywords: 'runners runs playbooks agents ai',
          run: () => void router.push('/factory'),
        },
      ]
    : []),
  {
    id: 'ui.theme',
    group: 'Appearance',
    label: 'Toggle theme',
    icon: '◐',
    shortcut: 'mod+shift+l',
    keywords: 'dark light system',
    run: () => ui.cycleTheme(),
  },
  {
    id: 'ui.density',
    group: 'Appearance',
    label: 'Toggle density',
    icon: '≡',
    keywords: 'compact comfortable',
    run: () => ui.setDensity(ui.density === 'compact' ? 'comfortable' : 'compact'),
  },
  {
    id: 'ui.sidebar',
    group: 'Appearance',
    label: 'Toggle sidebar',
    icon: '◧',
    shortcut: 'mod+b',
    run: toggleNavigation,
  },
  {
    id: 'org.create',
    group: 'Organization',
    label: 'New organization',
    icon: '＋',
    keywords: 'create workspace company',
    run: () => {
      creatingOrganization.value = true
    },
  },
  {
    id: 'org.settings',
    group: 'Organization',
    label: 'Organization settings',
    icon: '⚙',
    keywords: 'members invitations agents billing audit',
    when: () => organizations.currentSlug !== null,
    run: () => void router.push(orgSettingsPath(organizations.currentSlug!)),
  },
  {
    id: 'org.members',
    group: 'Organization',
    label: 'Members',
    icon: '☰',
    keywords: 'people roster roles invite',
    when: () => organizations.currentSlug !== null,
    run: () => void router.push(orgSettingsPath(organizations.currentSlug!, 'members')),
  },
  {
    id: 'project.settings',
    group: 'Project',
    label: 'Project settings',
    icon: '⚙',
    keywords: 'teams labels workflow archive',
    when: () => organizations.currentSlug !== null && projects.currentKey !== null,
    run: () =>
      void router.push(projectSettingsPath(organizations.currentSlug!, projects.currentKey!)),
  },
  {
    id: 'account.getting-started',
    group: 'Account',
    label: 'Get started',
    icon: '✓',
    keywords: 'onboarding checklist setup tour',
    run: () => onboarding.openChecklist(),
  },
  {
    id: 'account.replay-tour',
    group: 'Account',
    label: 'Replay product tour',
    icon: '↻',
    keywords: 'welcome orientation',
    run: () => onboarding.startTour(),
  },
  {
    id: 'account.settings',
    group: 'Account',
    label: 'Account settings',
    icon: '⚙',
    keywords: 'profile password tokens notifications',
    run: () => void router.push('/settings/profile'),
  },
  {
    id: 'session.logout',
    group: 'Account',
    label: 'Log out',
    icon: '⏻',
    keywords: 'sign out',
    run: () => void logout(),
  },
])

useShortcut('g then m', () => router.push('/'))
useShortcut('g then i', () => router.push('/items'))
useShortcut('g then b', () => router.push('/board'))
useShortcut('g then f', () => {
  if (organizations.current?.canOperateFactory) void router.push('/factory')
})
useShortcut('mod+shift+l', () => ui.cycleTheme(), { allowInInput: true })
useShortcut('mod+b', toggleNavigation, { allowInInput: true })
</script>

<template>
  <div class="flex h-screen overflow-hidden">
    <!-- Keyboard users should not have to tab the whole sidebar to reach the page. -->
    <a
      href="#main-content"
      class="bg-primary text-primary-foreground sr-only rounded px-3 py-2 text-sm focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-70"
    >
      Skip to content
    </a>

    <AppSidebar v-if="!isMobile" />

    <Sheet v-else :open="ui.mobileSidebarOpen" @update:open="ui.setMobileSidebarOpen($event)">
      <SheetContent
        side="left"
        class="max-w-none gap-0 border-0 p-0"
        :style="{ width: 'min(88vw, 320px)', maxWidth: 'none' }"
      >
        <SheetTitle class="sr-only">Navigation</SheetTitle>
        <SheetDescription class="sr-only">
          Switch organization, project, team, or application section.
        </SheetDescription>
        <AppSidebar mobile />
      </SheetContent>
    </Sheet>

    <div class="flex min-w-0 flex-1 flex-col">
      <AppHeader />
      <PaymentBanner
        v-if="billingSlug"
        :slug="billingSlug"
        :subscription="subscription.data.value"
        :is-owner="organizations.current?.role === 'owner'"
      />
      <main id="main-content" tabindex="-1" class="min-h-0 flex-1 overflow-y-auto">
        <slot />
      </main>
      <footer
        class="text-muted-foreground flex items-center justify-between gap-3 border-t px-3 py-2 text-xs sm:px-4"
      >
        <span>Aictiq {{ releaseMetadata.data.value?.version ?? 'development' }}</span>
        <a
          v-if="updateAvailable && latestRelease.data.value"
          :href="latestRelease.data.value.url"
          class="text-primary underline underline-offset-2"
          target="_blank"
          rel="noopener noreferrer"
        >
          Update available: {{ latestRelease.data.value.version }}
        </a>
      </footer>
    </div>

    <ItemDetailDialog />
    <ImageLightbox />

    <CommandPalette />
    <ShortcutHelp />
    <ProductTour v-if="tourWanted" />
    <GettingStartedPanel v-if="onboarding.checklistOpen" />
    <CreateOrganizationDialog v-model:open="creatingOrganization" @created="enterOrganization" />
  </div>
</template>
