<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'

import {
  externalLinkUrl,
  externalSignInUrl,
  listProviders,
  type ExternalProvider,
} from '@/api/auth'
import { Button } from '@/components/ui/button'

/**
 * The provider buttons - on the login and register pages to sign in, and on the security
 * page to attach a provider to an account that already exists.
 *
 * Nothing renders when no provider is configured - an instance with only password sign-in
 * is a supported deployment, and a row of dead buttons would be worse than none. The list
 * comes from the API rather than from a build-time flag, because it is the API that knows
 * which credentials it actually has.
 */
const props = withDefaults(
  defineProps<{
    next?: string
    invite?: string
    /** `link` attaches to the signed-in account instead of starting a new session. */
    mode?: 'signin' | 'link'
    /** Providers already attached, hidden in link mode - linking twice is a no-op. */
    linked?: string[]
  }>(),
  { next: undefined, invite: undefined, mode: 'signin', linked: () => [] },
)

const providers = ref<ExternalProvider[]>([])

const available = computed(() =>
  props.mode === 'link'
    ? providers.value.filter((provider) => !props.linked.includes(provider.name))
    : providers.value,
)

onMounted(async () => {
  try {
    providers.value = await listProviders()
  } catch {
    // The password form is the important half of this page; a failure here hides the
    // buttons rather than breaking sign-in.
    providers.value = []
  }
})

function start(provider: ExternalProvider) {
  // A full navigation, not a fetch: the first hop is a redirect to another origin.
  window.location.assign(
    props.mode === 'link'
      ? externalLinkUrl(provider.name, props.next ?? '/settings/security')
      : externalSignInUrl(provider.name, { next: props.next, invite: props.invite }),
  )
}
</script>

<template>
  <div v-if="available.length" class="space-y-3">
    <div v-if="mode === 'signin'" class="flex items-center gap-3">
      <span class="border-border h-px flex-1 border-t" aria-hidden="true"></span>
      <span class="text-muted-foreground text-xs">or</span>
      <span class="border-border h-px flex-1 border-t" aria-hidden="true"></span>
    </div>

    <Button
      v-for="provider in available"
      :key="provider.name"
      type="button"
      variant="outline"
      :class="mode === 'signin' ? 'w-full' : undefined"
      :size="mode === 'link' ? 'sm' : undefined"
      @click="start(provider)"
    >
      {{ mode === 'link' ? `Link ${provider.displayName}` : `Continue with ${provider.displayName}` }}
    </Button>
  </div>
</template>
