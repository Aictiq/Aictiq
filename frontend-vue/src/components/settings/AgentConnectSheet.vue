<script setup lang="ts">
import { Check, Copy } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import type { Agent } from '@/api/agents'
import { Button } from '@/components/ui/button'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import {
  claudeMdSnippet,
  cliLoginSnippet,
  httpMcpSnippet,
  mcpUrl,
  stdioMcpSnippet,
  type AgentSnippetContext,
} from '@/lib/agentSnippets'
import { useToast } from '@/composables/useToast'
import { factoryPath } from '@/router/paths'

/**
 * "Connect this agent" — the snippets a person pastes into a coding agent's configuration.
 *
 * The point is that nothing here has to be assembled by hand: the MCP URL is this
 * deployment's, and the token is the one just issued, if it is still on screen. A person
 * who has to work out their own origin and splice in a token gets it wrong once and then
 * debugs a 403 that says nothing about which half was wrong.
 */
const props = defineProps<{
  agent: Agent | null
  /** The organization whose Factory will run this agent. */
  slug: string
  /**
   * The secret from the token that was just created, shown exactly once. Absent on a
   * later visit, and the snippets then carry a placeholder — Aictiq cannot show a token
   * twice, and pretending otherwise would be the lie this screen exists to avoid.
   */
  secret?: string | null
}>()

const open = defineModel<boolean>('open', { required: true })

const toast = useToast()
const copied = ref<string | null>(null)
const tab = ref<'connect' | 'factory'>('connect')

const context = computed<AgentSnippetContext>(() => ({
  // The MCP endpoint is same-origin with the app: the API serves the SPA.
  origin: window.location.origin,
  secret: props.secret ?? null,
}))

const mcpEndpoint = computed(() => mcpUrl(context.value.origin))
const hasSecret = computed(() => Boolean(props.secret))
const name = computed(() => props.agent?.displayName ?? 'agent')

const httpSnippet = computed(() => httpMcpSnippet(context.value))
const stdioSnippet = computed(() => stdioMcpSnippet())
const cliSnippet = computed(() => cliLoginSnippet(context.value))
const claudeMd = computed(() => claudeMdSnippet(context.value))

async function copy(id: string, text: string) {
  try {
    await navigator.clipboard.writeText(text)
    copied.value = id
  } catch {
    toast.error(new Error('Could not copy — select the snippet and copy it manually.'))
  }
}

watch(open, (isOpen) => {
  if (!isOpen) {
    copied.value = null
    tab.value = 'connect'
  }
})
</script>

<template>
  <Sheet v-model:open="open">
    <SheetContent side="right" class="w-full gap-0 overflow-y-auto sm:max-w-xl">
      <SheetHeader>
        <SheetTitle>Connect {{ name }}</SheetTitle>
        <SheetDescription>
          Connect it to your own coding session, or let Aictiq run it on a Factory runner.
        </SheetDescription>
      </SheetHeader>

      <div class="space-y-5 px-4 pb-6">
        <div class="border-border flex items-center gap-4 border-b" role="tablist">
          <button
            id="agent-connect-tab"
            type="button"
            role="tab"
            :aria-selected="tab === 'connect'"
            aria-controls="agent-connect-panel"
            class="-mb-px border-b-2 px-1 py-2 text-[12.5px]"
            :class="
              tab === 'connect'
                ? 'border-primary text-foreground font-medium'
                : 'text-muted-foreground hover:text-foreground border-transparent'
            "
            @click="tab = 'connect'"
          >
            Connect it yourself
          </button>
          <button
            id="agent-factory-tab"
            type="button"
            role="tab"
            :aria-selected="tab === 'factory'"
            aria-controls="agent-factory-panel"
            class="-mb-px border-b-2 px-1 py-2 text-[12.5px]"
            :class="
              tab === 'factory'
                ? 'border-primary text-foreground font-medium'
                : 'text-muted-foreground hover:text-foreground border-transparent'
            "
            @click="tab = 'factory'"
          >
            Run it from Aictiq
          </button>
        </div>

        <div
          v-if="tab === 'connect'"
          id="agent-connect-panel"
          role="tabpanel"
          aria-labelledby="agent-connect-tab"
          class="space-y-5"
        >
          <p class="text-muted-foreground text-xs">
            This instance's MCP endpoint is <code class="font-mono">{{ mcpEndpoint }}</code
            >. It needs a token with the <code class="font-mono">mcp</code> scope; tokens created
            here are already bound to this organization.
          </p>

          <p
            v-if="!hasSecret"
            class="border-border text-muted-foreground rounded-lg border border-dashed p-2.5 text-xs"
          >
            A token is shown only when it is created, so the snippets below carry a placeholder.
            Create a new token for {{ name }} and this drawer will fill it in.
          </p>

          <section class="space-y-1.5">
            <div class="flex items-center justify-between gap-2">
              <h3 class="text-sm font-medium">Claude Code, over HTTP</h3>
              <Button
                variant="ghost"
                size="sm"
                :aria-label="`Copy the HTTP configuration for ${name}`"
                @click="copy('http', httpSnippet)"
              >
                <Check v-if="copied === 'http'" class="size-3.5" aria-hidden="true" />
                <Copy v-else class="size-3.5" aria-hidden="true" />
                Copy
              </Button>
            </div>
            <p class="text-muted-foreground text-xs">
              In the repository's <code class="font-mono">.mcp.json</code>. The token is in the
              file, so keep it out of version control.
            </p>
            <pre
              class="bg-muted/50 border-border overflow-x-auto rounded-lg border p-2.5 font-mono text-[11px]"
            ><code>{{ httpSnippet }}</code></pre>
          </section>

          <section class="space-y-1.5">
            <div class="flex items-center justify-between gap-2">
              <h3 class="text-sm font-medium">Claude Code, through the CLI</h3>
              <Button
                variant="ghost"
                size="sm"
                :aria-label="`Copy the stdio configuration for ${name}`"
                @click="copy('stdio', stdioSnippet)"
              >
                <Check v-if="copied === 'stdio'" class="size-3.5" aria-hidden="true" />
                <Copy v-else class="size-3.5" aria-hidden="true" />
                Copy
              </Button>
            </div>
            <p class="text-muted-foreground text-xs">
              The CLI keeps the token in
              <code class="font-mono">~/.config/aictiq/config.json</code> (mode 0600), so this
              <code class="font-mono">.mcp.json</code> holds no secret and can be committed.
            </p>
            <pre
              class="bg-muted/50 border-border overflow-x-auto rounded-lg border p-2.5 font-mono text-[11px]"
            ><code>{{ cliSnippet }}</code></pre>
            <pre
              class="bg-muted/50 border-border overflow-x-auto rounded-lg border p-2.5 font-mono text-[11px]"
            ><code>{{ stdioSnippet }}</code></pre>
          </section>

          <section class="space-y-1.5">
            <div class="flex items-center justify-between gap-2">
              <h3 class="text-sm font-medium">A block for the repository's CLAUDE.md</h3>
              <Button
                variant="ghost"
                size="sm"
                aria-label="Copy the CLAUDE.md block"
                @click="copy('claude-md', claudeMd)"
              >
                <Check v-if="copied === 'claude-md'" class="size-3.5" aria-hidden="true" />
                <Copy v-else class="size-3.5" aria-hidden="true" />
                Copy
              </Button>
            </div>
            <p class="text-muted-foreground text-xs">
              Tells the agent the loop: claim, read, branch, report, link, transition.
            </p>
            <pre
              class="bg-muted/50 border-border overflow-x-auto rounded-lg border p-2.5 font-mono text-[11px]"
            ><code>{{ claudeMd }}</code></pre>
          </section>

          <p class="text-muted-foreground text-xs">
            The full playbook — the loop, heartbeats, what a 409 means — is in
            <code class="font-mono">docs/agents.md</code>.
          </p>
        </div>

        <div
          v-else
          id="agent-factory-panel"
          role="tabpanel"
          aria-labelledby="agent-factory-tab"
          class="space-y-4"
        >
          <div class="border-border rounded-lg border p-4">
            <h3 class="text-sm font-medium">Give {{ name }} a playbook and a runner</h3>
            <p class="text-muted-foreground mt-1 text-xs">
              Factory queues an item for this agent, runs it on a machine you control, and brings
              its logs and pull request back to Aictiq. Each run receives a short-lived agent token
              that is revoked when the run ends.
            </p>
          </div>
          <ol class="text-muted-foreground list-decimal space-y-2 pl-5 text-xs">
            <li>Register a runner with a supported harness already signed in.</li>
            <li>Create a playbook with the instructions this agent should follow.</li>
            <li>Hand an item to the agent, then follow the run from Factory.</li>
          </ol>
          <Button as-child>
            <RouterLink :to="factoryPath(slug, 'runners')">Open Factory</RouterLink>
          </Button>
        </div>
      </div>
    </SheetContent>
  </Sheet>
</template>
