import { nextTick } from 'vue'
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'

import UiPageState from '@/components/UiPageState.vue'
import { factoryPath, factoryRunPath, orgSettingsPath, projectSettingsPath, teamSettingsPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useSessionStore } from '@/stores/session'

declare module 'vue-router' {
  interface RouteMeta {
    /** Signed-in only. Anonymous visitors are sent to /login with a `next` back-link. */
    requiresAuth: boolean
    /** Signed-out only - the auth pages. A signed-in visitor is bounced to the app. */
    guestOnly?: boolean
    /** Heading for placeholder screens. */
    title?: string
    /** The screen is not built yet; PlaceholderView stands in for it. */
    soon?: boolean
  }
}

/** Sidebar destinations whose modules do not exist yet. */
const placeholders: RouteRecordRaw[] = (
  [
    ['/items', 'items', 'Items'],
    ['/backlog', 'backlog', 'Backlog'],
    ['/agents', 'agents', 'Agents'],
  ] as const
).map(([path, name, title]) => ({
  path,
  name,
  component: () => import('@/views/PlaceholderView.vue'),
  meta: { requiresAuth: true, title, soon: true },
}))

/**
 * The paths this app shipped with before the settings hub existed. They are kept because
 * they are in people's history and in mails already sent - and they are *routes* rather
 * than static redirects because the target needs the organization the visitor is in,
 * which is only known once the organization list has loaded.
 *
 * The component is never rendered: `beforeEnter` always answers with a location.
 */
function legacyRedirect(
  path: string,
  name: string,
  to: (slug: string, params: Record<string, string>) => string,
): RouteRecordRaw {
  return {
    path,
    name,
    component: UiPageState,
    props: { state: 'loading' },
    meta: { requiresAuth: true },
    beforeEnter: async (target) => {
      const organizations = useOrganizationsStore()
      if (!organizations.isResolved) await organizations.load()

      const slug = organizations.currentSlug
      // Nowhere to send them: they belong to no organization, and the home page is where
      // creating the first one is offered.
      if (!slug) return { path: '/' }

      const params = Object.fromEntries(
        Object.entries(target.params).map(([key, value]) => [
          key,
          Array.isArray(value) ? (value[0] ?? '') : value,
        ]),
      )
      return { path: to(slug, params) }
    },
  }
}

const routes: RouteRecordRaw[] = [
  { path: '/inbox', name: 'inbox', component: () => import('@/views/InboxView.vue'), meta: { requiresAuth: true, title: 'Inbox' } },
  {
    path: '/',
    name: 'home',
    component: () => import('@/views/HomeView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/o/:slug/p/:projectKey/dashboard', name: 'project-dashboard',
    component: () => import('@/views/DashboardView.vue'),
    props: route => ({ slug: String(route.params.slug), projectKey: String(route.params.projectKey) }),
    meta: { requiresAuth: true, title: 'Dashboard' },
  },
  {
    path: '/o/:slug/p/:projectKey/analytics', name: 'project-analytics',
    component: () => import('@/views/AnalyticsView.vue'),
    props: route => ({ slug: String(route.params.slug), projectKey: String(route.params.projectKey) }),
    meta: { requiresAuth: true, title: 'Cycle insights' },
  },
  {
    path: '/onboarding',
    name: 'onboarding',
    component: () => import('@/views/OnboardingView.vue'),
    meta: { requiresAuth: true, title: 'Get started' },
  },
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/LoginView.vue'),
    meta: { requiresAuth: false, guestOnly: true },
  },
  {
    path: '/register',
    name: 'register',
    component: () => import('@/views/RegisterView.vue'),
    meta: { requiresAuth: false, guestOnly: true },
  },
  {
    path: '/forgot-password',
    name: 'forgot-password',
    component: () => import('@/views/ForgotPasswordView.vue'),
    meta: { requiresAuth: false, guestOnly: true },
  },
  {
    // Public: the whole point of an invitation is that the recipient may not have an
    // account yet, so the page explains itself first and offers sign-in second.
    path: '/invite/:token',
    name: 'invite',
    component: () => import('@/views/InviteAcceptView.vue'),
    meta: { requiresAuth: false },
  },
  {
    // Not guestOnly: the link arrives by mail and is opened wherever the mail was read,
    // which is quite often a browser that is already signed in as somebody.
    path: '/reset-password/:token',
    name: 'reset-password',
    component: () => import('@/views/ResetPasswordView.vue'),
    meta: { requiresAuth: false },
  },
  {
    // Anonymous by design: the confirmation link proves control of the new mailbox, and
    // requiring a session as well would strand anyone who read the mail on their phone.
    path: '/confirm-email/:token',
    name: 'confirm-email',
    component: () => import('@/views/ConfirmEmailView.vue'),
    meta: { requiresAuth: false },
  },
  {
    path: '/projects',
    name: 'projects',
    component: () => import('@/views/ProjectsView.vue'),
    meta: { requiresAuth: true, title: 'Projects' },
  },
  {
    path: '/search',
    name: 'search',
    component: () => import('@/views/SearchView.vue'),
    meta: { requiresAuth: true, title: 'Search' },
  },
  {
    path: '/o/:slug/p/:projectKey/items',
    name: 'project-items',
    component: () => import('@/views/ItemListView.vue'),
    props: (route) => ({ slug: String(route.params.slug), projectKey: String(route.params.projectKey) }),
    meta: { requiresAuth: true, title: 'Items' },
  },
  {
    path: '/o/:slug/p/:projectKey/wiki/:pageId?/:wikiSlug?',
    name: 'project-wiki',
    component: () => import('@/views/WikiView.vue'),
    props: (route) => ({ slug: String(route.params.slug), projectKey: String(route.params.projectKey), pageId: route.params.pageId ? String(route.params.pageId) : undefined }),
    meta: { requiresAuth: true, title: 'Wiki' },
  },
  {
    path: '/o/:slug/p/:projectKey/items/:itemKey',
    name: 'item-detail',
    component: () => import('@/views/ItemDetailView.vue'),
    props: (route) => ({ slug: String(route.params.slug), projectKey: String(route.params.projectKey), itemKey: String(route.params.itemKey) }),
    meta: { requiresAuth: true, title: 'Item' },
  },
  {
    path: '/o/:slug/p/:projectKey/teams/:teamId/backlog',
    name: 'team-backlog',
    component: () => import('@/views/BacklogView.vue'),
    props: (route) => ({ slug: String(route.params.slug), projectKey: String(route.params.projectKey), teamId: String(route.params.teamId) }),
    meta: { requiresAuth: true, title: 'Backlog' },
  },
  {
    path: '/o/:slug/p/:projectKey/teams/:teamId/sprints',
    name: 'team-sprints',
    component: () => import('@/views/SprintsView.vue'),
    props: (route) => ({
      slug: String(route.params.slug),
      projectKey: String(route.params.projectKey),
      teamId: String(route.params.teamId),
    }),
    meta: { requiresAuth: true, title: 'Sprints' },
  },
  {
    path: '/o/:slug/p/:projectKey/teams/:teamId/sprints/:sprintId',
    name: 'sprint-detail',
    component: () => import('@/views/SprintDetailView.vue'),
    props: (route) => ({
      slug: String(route.params.slug),
      projectKey: String(route.params.projectKey),
      teamId: String(route.params.teamId),
      sprintId: String(route.params.sprintId),
    }),
    meta: { requiresAuth: true, title: 'Sprint' },
  },
  {
    path: '/o/:slug/portfolio',
    name: 'organization-portfolio',
    component: () => import('@/views/PortfolioView.vue'),
    props: (route) => ({ slug: String(route.params.slug) }),
    meta: { requiresAuth: true, title: 'Portfolio' },
  },
  {
    path: '/o/:slug/p/:projectKey/portfolio',
    name: 'project-portfolio',
    component: () => import('@/views/PortfolioView.vue'),
    props: (route) => ({
      slug: String(route.params.slug),
      projectKey: String(route.params.projectKey),
    }),
    meta: { requiresAuth: true, title: 'Portfolio' },
  },
  {
    // The current organization/project/team live in their stores, just like the rail's
    // other team-level destinations. The board itself still names those values on every API call.
    path: '/board',
    name: 'board',
    component: () => import('@/views/BoardView.vue'),
    meta: { requiresAuth: true, title: 'Board' },
  },

  // ── Personal settings ────────────────────────────────────────────────────────────
  // No slug: a password, an avatar and a personal access token follow the account across
  // every organization it is in.
  {
    path: '/settings',
    component: () => import('@/views/settings/PersonalSettingsLayout.vue'),
    meta: { requiresAuth: true },
    children: [
      { path: '', redirect: { name: 'profile-settings' } },
      {
        path: 'profile',
        name: 'profile-settings',
        component: () => import('@/views/settings/ProfileSettingsView.vue'),
        meta: { requiresAuth: true, title: 'Profile' },
      },
      {
        path: 'security',
        name: 'security-settings',
        component: () => import('@/views/settings/SecuritySettingsView.vue'),
        meta: { requiresAuth: true, title: 'Security' },
      },
      {
        path: 'tokens',
        name: 'access-tokens',
        component: () => import('@/views/settings/AccessTokensView.vue'),
        meta: { requiresAuth: true, title: 'Access tokens' },
      },
      {
        path: 'notifications',
        name: 'notification-settings',
        component: () => import('@/views/settings/NotificationSettingsView.vue'),
        meta: { requiresAuth: true, title: 'Notifications' },
      },
    ],
  },

  // ── The AI software factory ──────────────────────────────────────────────────────
  // Top level rather than a settings tab: runners and runs are where work happens, not
  // configuration. Agent identities and their tokens stay in organization settings.
  {
    path: '/o/:slug/factory',
    component: () => import('@/views/factory/FactoryLayout.vue'),
    meta: { requiresAuth: true },
    children: [
      {
        path: '',
        name: 'factory',
        redirect: (to) => factoryPath(String(to.params.slug)),
      },
      {
        path: 'runs',
        name: 'factory-runs',
        component: () => import('@/views/factory/RunsTab.vue'),
        meta: { requiresAuth: true, title: 'Runs' },
      },
      {
        // A deep link to one run. The layout's operator gate is what answers a link a
        // stakeholder was handed: the same refusal the tabs get, and the API's 404s back it up.
        path: 'runs/:runId',
        name: 'factory-run-detail',
        component: () => import('@/views/factory/RunDetailView.vue'),
        meta: { requiresAuth: true, title: 'Run' },
      },
      {
        path: 'rules',
        name: 'factory-rules',
        component: () => import('@/views/factory/RulesTab.vue'),
        meta: { requiresAuth: true, title: 'Rules' },
      },
      {
        // A deep link to one rule, resolved by id alone - see RuleDetailView for why.
        path: 'rules/:ruleId',
        name: 'factory-rule-detail',
        component: () => import('@/views/factory/RuleDetailView.vue'),
        meta: { requiresAuth: true, title: 'Rule' },
      },
      {
        path: 'runners',
        name: 'factory-runners',
        component: () => import('@/views/factory/FactoryRunnersView.vue'),
        meta: { requiresAuth: true, title: 'Runners' },
      },
      {
        path: 'playbooks',
        name: 'factory-playbooks',
        component: () => import('@/views/factory/FactoryPlaybooksView.vue'),
        meta: { requiresAuth: true, title: 'Playbooks' },
      },
    ],
  },

  // ── Organization settings ────────────────────────────────────────────────────────
  {
    path: '/o/:slug/settings',
    component: () => import('@/views/settings/OrgSettingsLayout.vue'),
    meta: { requiresAuth: true },
    children: [
      {
        path: '',
        name: 'org-settings',
        redirect: (to) => orgSettingsPath(String(to.params.slug)),
      },
      {
        path: 'general',
        name: 'org-settings-general',
        component: () => import('@/views/settings/OrgGeneralView.vue'),
        meta: { requiresAuth: true, title: 'General' },
      },
      {
        path: 'members',
        name: 'org-settings-members',
        component: () => import('@/views/settings/OrgMembersView.vue'),
        meta: { requiresAuth: true, title: 'Members' },
      },
      {
        path: 'invitations',
        name: 'org-settings-invitations',
        component: () => import('@/views/settings/OrgInvitationsView.vue'),
        meta: { requiresAuth: true, title: 'Invitations' },
      },
      {
        path: 'agents',
        name: 'org-settings-agents',
        component: () => import('@/views/settings/OrgAgentsView.vue'),
        meta: { requiresAuth: true, title: 'Agents' },
      },
      { path: 'billing', name: 'org-settings-billing', component: () => import('@/views/settings/OrgBillingView.vue'), meta: { requiresAuth: true, title: 'Billing' } },
      {
        path: 'integrations',
        name: 'org-settings-integrations',
        component: () => import('@/views/settings/OrgWebhooksView.vue'),
        meta: { requiresAuth: true, title: 'Integrations' },
      },
      { path: 'audit', name: 'org-settings-audit', component: () => import('@/views/settings/OrgAuditLogView.vue'), props: route => ({ slug: String(route.params.slug) }), meta: { requiresAuth: true, title: 'Audit log' } },
    ],
  },

  // ── Project settings ─────────────────────────────────────────────────────────────
  // Nested under the organization because the organization is what establishes the
  // tenant, and addressed by the project's permanent key rather than its id.
  {
    path: '/o/:slug/p/:projectKey/settings',
    component: () => import('@/views/settings/ProjectSettingsLayout.vue'),
    meta: { requiresAuth: true },
    children: [
      {
        path: '',
        name: 'project-settings',
        redirect: (to) =>
          projectSettingsPath(String(to.params.slug), String(to.params.projectKey)),
      },
      {
        path: 'general',
        name: 'project-settings-general',
        component: () => import('@/views/settings/ProjectGeneralView.vue'),
        meta: { requiresAuth: true, title: 'General' },
      },
      {
        path: 'factory',
        name: 'project-settings-factory',
        component: () => import('@/views/settings/ProjectFactoryView.vue'),
        meta: { requiresAuth: true, title: 'Factory' },
      },
      {
        path: 'members',
        name: 'project-settings-members',
        component: () => import('@/views/settings/ProjectMembersView.vue'),
        meta: { requiresAuth: true, title: 'Members' },
      },
      {
        path: 'teams',
        name: 'project-settings-teams',
        component: () => import('@/views/settings/ProjectTeamsView.vue'),
        meta: { requiresAuth: true, title: 'Teams' },
      },
      {
        // One team, inside the project's own hub: a team is not addressable without the
        // project that owns it, and the API answers 404 to one that is.
        path: 'teams/:teamId',
        name: 'team-settings',
        component: () => import('@/views/settings/ProjectTeamView.vue'),
        meta: { requiresAuth: true, title: 'Team' },
      },
      {
        path: 'workflow',
        name: 'project-settings-workflow',
        component: () => import('@/views/settings/ProjectWorkflowView.vue'),
        meta: { requiresAuth: true, title: 'Workflow' },
      },
      {
        path: 'labels',
        name: 'project-settings-labels',
        component: () => import('@/views/settings/ProjectLabelsView.vue'),
        meta: { requiresAuth: true, title: 'Labels' },
      },
      {
        path: 'templates',
        name: 'project-settings-templates',
        component: () => import('@/views/settings/ProjectTemplatesView.vue'),
        meta: { requiresAuth: true, title: 'Templates' },
      },
      {
        path: 'integrations',
        name: 'project-settings-integrations',
        component: () => import('@/views/settings/ProjectIntegrationsView.vue'),
        meta: { requiresAuth: true, title: 'Integrations' },
      },
    ],
  },

  // ── Paths that predate the hub ───────────────────────────────────────────────────
  legacyRedirect('/settings/organization', 'org-settings-legacy', (slug) =>
    orgSettingsPath(slug, 'general'),
  ),
  legacyRedirect('/members', 'members-legacy', (slug) => orgSettingsPath(slug, 'members')),
  // What `g then f` and the rail fall back to before an organization is known.
  legacyRedirect('/factory', 'factory-current', (slug) => factoryPath(slug)),
  // A finished run's comment on the item links `/runs/{id}`: the handler that writes it
  // knows the organization's id, not its slug, and the item page is already inside it.
  legacyRedirect('/runs/:runId', 'run-current', (slug, params) =>
    factoryRunPath(slug, params.runId ?? ''),
  ),
  legacyRedirect('/projects/:projectKey/settings', 'project-settings-legacy', (slug, params) =>
    projectSettingsPath(slug, params.projectKey ?? ''),
  ),
  legacyRedirect(
    '/projects/:projectKey/teams/:teamId/settings',
    'team-settings-legacy',
    (slug, params) => teamSettingsPath(slug, params.projectKey ?? '', params.teamId ?? ''),
  ),

  ...placeholders,
  // Development only: a production build has no route to it at all, rather than a route
  // that merely refuses to render.
  ...(import.meta.env.DEV
    ? [
        {
          path: '/dev/kitchen-sink',
          name: 'kitchen-sink',
          component: () => import('@/views/KitchenSinkView.vue'),
          meta: { requiresAuth: true },
        } satisfies RouteRecordRaw,
      ]
    : []),
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { requiresAuth: false },
  },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
  scrollBehavior: (_to, _from, saved) => saved ?? { top: 0 },
})

/**
 * A scoped URL names the organization and the project it acts on, so the URL - not the
 * remembered choice - is what decides where the shell thinks it is. Opening a link
 * somebody pasted must move the sidebar with it, or the page and the rail beside it would
 * disagree about which organization is being looked at. That matters most to people who
 * are in several: an Owner in one and a stakeholder in another must never see one
 * organization's controls while reading the other's work.
 *
 * A slug the caller is *not* in is deliberately not selected. The page will answer 404 on
 * its own, and pointing the shell at an organization someone does not belong to empties
 * the sidebar and the switcher for the rest of the session. An unknown slug is re-read
 * once first, because a membership accepted in another tab is not in a list read at boot.
 */
async function syncScopeWithRoute(params: Record<string, unknown>) {
  const slug = typeof params.slug === 'string' ? params.slug : null
  const projectKey = typeof params.projectKey === 'string' ? params.projectKey : null

  const organizations = useOrganizationsStore()
  if (slug && slug !== organizations.currentSlug) {
    if (!organizations.isResolved) await organizations.load()
    const isMine = () => organizations.organizations.some((o) => o.slug === slug)
    if (!isMine()) await organizations.reload()
    // Still not theirs: leave the shell pointing where it is - and leave the project key
    // alone too, since a key only means something inside its own organization.
    if (!isMine()) return
    organizations.select(slug)
    // The projects store watches the organization and resets its own selection when it
    // changes; let that run before naming the project, or it would clear it again.
    await nextTick()
  }

  if (projectKey) {
    const projects = useProjectsStore()
    if (projectKey !== projects.currentKey) projects.select(projectKey)
  }
}

/**
 * One guard for both directions. `load()` is single-flight, so the first navigation pays
 * for the session round trip and every later one reads the resolved store.
 */
router.beforeEach(async (to) => {
  const session = useSessionStore()

  if (!session.isResolved) {
    await session.load()
  }

  if (to.meta.requiresAuth && !session.isAuthenticated) {
    // `next` brings them back to where they were aiming once they sign in.
    return { name: 'login', query: { next: to.fullPath } }
  }

  if (to.meta.guestOnly && session.isAuthenticated) {
    return { path: typeof to.query.next === 'string' ? to.query.next : '/' }
  }

  if (session.isAuthenticated) await syncScopeWithRoute(to.params)

  return true
})

export default router
