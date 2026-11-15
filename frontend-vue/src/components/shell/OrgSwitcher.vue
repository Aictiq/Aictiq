<script setup lang="ts">
import { Check, ChevronsUpDown, Plus, Settings } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'

import CreateOrganizationDialog from '@/components/shell/CreateOrganizationDialog.vue'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { useOrganizationSwitch } from '@/composables/useOrganizationSwitch'
import { orgSettingsPath } from '@/router/paths'
import { useOrganizationsStore } from '@/stores/organizations'
import { useUiStore } from '@/stores/ui'

/**
 * The sidebar's top block: which organization you are in, and how to get to another.
 *
 * The *list* of organizations only appears when there is more than one - offering to
 * switch to the single one you are already in is noise. The menu itself stays, because
 * creating the second organization has to be reachable from somewhere, and it is also in
 * the command palette.
 */
const organizations = useOrganizationsStore()
const ui = useUiStore()
const router = useRouter()
const enter = useOrganizationSwitch()

const props = withDefaults(defineProps<{ collapsed?: boolean; mobile?: boolean }>(), {
  collapsed: undefined,
  mobile: false,
})

const collapsed = computed(() => props.collapsed ?? ui.sidebarCollapsed)

const creating = ref(false)

const others = computed(() =>
  organizations.organizations.filter((o) => o.slug !== organizations.currentSlug),
)

/** Two letters is what fits the 22px mark in the design and still tells orgs apart. */
const initials = computed(() =>
  (organizations.current?.name ?? 'Aictiq')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((word) => word[0]!.toUpperCase())
    .join(''),
)

function switchTo(slug: string) {
  enter(slug)
  if (props.mobile) ui.setMobileSidebarOpen(false)
}

function openOrganizationSettings() {
  if (!organizations.currentSlug) return
  if (props.mobile) ui.setMobileSidebarOpen(false)
  void router.push(orgSettingsPath(organizations.currentSlug))
}
</script>

<template>
  <div class="border-sidebar-border border-b p-2.5" :class="mobile && 'mobile-org-switcher'">
    <DropdownMenu>
      <DropdownMenuTrigger
        data-tour="org-switcher"
        class="hover:bg-sidebar-accent focus-visible:ring-ring flex w-full items-center gap-2 rounded p-0.5 text-left focus-visible:ring-2 focus-visible:outline-none"
        :aria-label="`Organization: ${organizations.current?.name ?? 'none selected'}`"
      >
        <div
          class="bg-primary text-primary-foreground grid size-[22px] flex-none place-items-center rounded font-mono text-[9px] font-semibold"
          aria-hidden="true"
        >
          {{ initials }}
        </div>
        <div v-if="!collapsed" class="min-w-0 flex-1">
          <div class="truncate text-[13px] font-semibold tracking-tight">
            {{ organizations.current?.name ?? 'Aictiq' }}
          </div>
          <div class="font-label">
            {{ organizations.current ? organizations.current.role : 'No organization' }}
          </div>
        </div>
        <ChevronsUpDown
          v-if="!collapsed"
          class="text-muted-foreground size-3 flex-none"
          aria-hidden="true"
        />
      </DropdownMenuTrigger>

      <DropdownMenuContent align="start" class="w-56" :class="mobile && 'mobile-navigation-menu'">
        <template v-if="others.length > 0">
          <DropdownMenuLabel class="font-label">Switch organization</DropdownMenuLabel>
          <DropdownMenuItem
            v-for="organization in others"
            :key="organization.id"
            @select="switchTo(organization.slug)"
          >
            <span class="flex-1 truncate">{{ organization.name }}</span>
            <Check
              v-if="organization.slug === organizations.currentSlug"
              class="size-3.5"
              aria-hidden="true"
            />
          </DropdownMenuItem>
          <DropdownMenuSeparator />
        </template>

        <DropdownMenuItem
          v-if="organizations.current?.canOperateFactory"
          @select="openOrganizationSettings"
        >
          <Settings class="size-3.5" aria-hidden="true" />
          Organization settings
        </DropdownMenuItem>
        <DropdownMenuItem @select="creating = true">
          <Plus class="size-3.5" aria-hidden="true" />
          New organization…
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>

    <!-- A new organization is one you are now in: the page moves with the selection. -->
    <CreateOrganizationDialog v-model:open="creating" @created="switchTo" />
  </div>
</template>

<style scoped>
.mobile-org-switcher {
  flex-shrink: 0;
  padding: 6px 44px 6px 6px;
}

.mobile-org-switcher [data-tour='org-switcher'] {
  min-height: 44px;
  padding: 4px 6px;
}

.mobile-org-switcher .tracking-tight {
  font-size: 16px;
  line-height: 22px;
}

.mobile-org-switcher .font-label {
  font-size: 12px;
  line-height: 18px;
  letter-spacing: 0.08em;
}
</style>
