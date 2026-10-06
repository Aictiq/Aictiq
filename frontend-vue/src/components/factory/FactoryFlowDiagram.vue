<script setup lang="ts">
import { Bot, GitPullRequest, Pause, Play, Server, Ticket, Workflow } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, type Component } from 'vue'

import { Button } from '@/components/ui/button'

/**
 * How one run travels, drawn: the ticket, Aictiq's queue, the runner on your machine, the
 * harness inside it, the pull request, and the outcome back on the ticket. It plays on its
 * own so a newcomer can watch the whole loop once; hovering, focusing or clicking a stage
 * holds it there. With reduced motion it never moves, and the same story is a plain list for
 * a screen reader.
 */

interface Stage {
  title: string
  where: string
  body: string
  icon: Component | null
}

const stages: Stage[] = [
  {
    title: 'Ticket',
    where: 'Aictiq',
    body: 'Someone writes the item: the outcome, the context, and how to tell it is done. Attachments travel with it.',
    icon: Ticket,
  },
  {
    title: 'Run queued',
    where: 'Aictiq',
    body: 'Hand to agent, or a rule, queues a run: one item, one agent, one playbook. Nothing runs without that step.',
    icon: Workflow,
  },
  {
    title: 'Runner claims it',
    where: 'Your machine',
    body: 'Your runner polls Aictiq, claims the run and prepares a clean worktree of the repository beside your checkout.',
    icon: Server,
  },
  {
    title: 'Agent works',
    where: 'Your machine',
    body: 'Claude Code, Codex, OpenCode, Cursor or GitHub Copilot follows the playbook as the agent, with the Aictiq MCP server and a token that only works for this run.',
    icon: Bot,
  },
  {
    title: 'Pull request',
    where: 'Your git host',
    body: 'The work leaves as a branch and a pull request, or a direct push when the playbook says so.',
    icon: GitPullRequest,
  },
  {
    title: 'Outcome',
    where: 'Aictiq',
    body: 'Aictiq links the pull request, comments the outcome and moves the item. A person reviews the diff before it counts.',
    icon: null,
  },
]

/** Where each node sits in the 760×230 drawing; the sixth stage is the arc home. */
const nodes = [
  { x: 80, y: 92 },
  { x: 222, y: 92 },
  { x: 390, y: 92 },
  { x: 532, y: 92 },
  { x: 684, y: 92 },
]

const regions = [
  { x: 14, width: 280, label: 'Aictiq' },
  { x: 314, width: 290, label: 'Your runner machine' },
  { x: 622, width: 124, label: 'Git host' },
]

const returnArc = 'M684 124 C684 222 80 222 80 124'

const stage = ref(0)
const playing = ref(true)
const held = ref(false)
/** Wrapping from the outcome back to the ticket jumps rather than sliding backwards. */
const jumping = ref(false)
const reducedMotion = ref(false)

let timer: ReturnType<typeof setInterval> | undefined

function advance() {
  if (!playing.value || held.value || reducedMotion.value) return
  const next = (stage.value + 1) % stages.length
  jumping.value = next === 0
  stage.value = next
}

onMounted(() => {
  reducedMotion.value =
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(prefers-reduced-motion: reduce)').matches
  if (reducedMotion.value) playing.value = false
  timer = setInterval(advance, 2600)
})
onBeforeUnmount(() => clearInterval(timer))

function select(index: number) {
  jumping.value = false
  stage.value = index
  playing.value = false
}

const current = computed(() => stages[stage.value]!)

/** The packet rides the nodes; on the last stage it is travelling the arc instead. */
const dot = computed(() => nodes[Math.min(stage.value, nodes.length - 1)]!)
</script>

<template>
  <figure
    class="border-border bg-card relative overflow-hidden rounded-xl border"
    data-testid="factory-flow"
  >
    <figcaption class="flex items-start justify-between gap-3 px-4 pt-3">
      <div>
        <p class="text-sm font-medium">How a run travels</p>
        <p class="text-muted-foreground text-xs">
          Aictiq keeps the work and the rules; your runner does the work, on your machine.
        </p>
      </div>
      <Button
        v-if="!reducedMotion"
        variant="ghost"
        size="icon"
        :aria-label="playing ? 'Pause the animation' : 'Play the animation'"
        data-testid="factory-flow-toggle"
        @click="playing = !playing"
      >
        <Pause v-if="playing" class="size-4" aria-hidden="true" />
        <Play v-else class="size-4" aria-hidden="true" />
      </Button>
    </figcaption>

    <svg
      viewBox="0 0 760 230"
      class="mx-auto block h-auto w-full max-w-3xl select-none"
      aria-hidden="true"
      @mouseenter="held = true"
      @mouseleave="held = false"
    >
      <defs>
        <radialGradient id="flow-glow">
          <stop offset="0%" stop-color="var(--primary)" stop-opacity="0.55" />
          <stop offset="100%" stop-color="var(--primary)" stop-opacity="0" />
        </radialGradient>
      </defs>

      <g v-for="region in regions" :key="region.label">
        <rect
          :x="region.x"
          y="14"
          :width="region.width"
          height="160"
          rx="14"
          class="fill-muted/40 stroke-border"
          stroke-dasharray="4 4"
        />
        <text
          :x="region.x + 12"
          y="32"
          class="fill-muted-foreground font-mono text-[9.5px] tracking-[0.14em] uppercase"
        >
          {{ region.label }}
        </text>
      </g>

      <!-- The road between stages; the part already travelled is lit. -->
      <g v-for="(node, index) in nodes.slice(0, -1)" :key="`seg-${index}`">
        <line
          :x1="node.x + 30"
          :y1="node.y"
          :x2="nodes[index + 1]!.x - 30"
          :y2="node.y"
          class="stroke-border"
          stroke-width="2"
        />
        <line
          :x1="node.x + 30"
          :y1="node.y"
          :x2="nodes[index + 1]!.x - 30"
          :y2="node.y"
          class="flow-lit stroke-primary"
          stroke-width="2.5"
          pathLength="1"
          :style="{ strokeDashoffset: stage > index ? 0 : 1 }"
        />
      </g>

      <!-- The way home: the outcome goes back to the ticket. -->
      <path
        :d="returnArc"
        fill="none"
        class="stroke-border"
        stroke-width="2"
        stroke-dasharray="3 6"
      />
      <path
        :d="returnArc"
        fill="none"
        class="flow-return stroke-primary"
        stroke-width="2.5"
        stroke-dasharray="6 8"
        :class="{ 'flow-return-active': stage === 5 }"
      />
      <text x="382" y="215" text-anchor="middle" class="fill-muted-foreground text-[10px]">
        outcome comment · PR link · workflow transition
      </text>

      <g
        v-for="(node, index) in nodes"
        :key="`node-${index}`"
        class="flow-node cursor-pointer"
        :class="{ 'flow-node-active': stage === index || (stage === 5 && index === 0) }"
        @click="select(index)"
      >
        <circle :cx="node.x" :cy="node.y" r="44" fill="url(#flow-glow)" class="flow-halo" />
        <circle
          :cx="node.x"
          :cy="node.y"
          r="28"
          class="stroke-border fill-background"
          :class="{ 'stroke-primary': stage >= index }"
          stroke-width="2"
        />
        <component
          :is="stages[index]!.icon"
          :x="node.x - 11"
          :y="node.y - 11"
          width="22"
          height="22"
          :class="stage >= index ? 'text-primary' : 'text-muted-foreground'"
        />
        <text
          :x="node.x"
          :y="node.y + 48"
          text-anchor="middle"
          class="fill-foreground text-[11px] font-medium"
        >
          {{ stages[index]!.title }}
        </text>
      </g>

      <!-- The run itself. -->
      <g
        class="flow-packet"
        :class="{ 'flow-packet-jump': jumping, 'opacity-0': stage === 5 }"
        :style="{ transform: `translate(${dot.x}px, ${dot.y - 34}px)` }"
      >
        <circle r="6" class="fill-primary" />
        <circle r="6" class="flow-ping fill-primary" />
      </g>
    </svg>

    <div
      class="border-border flex items-start gap-3 border-t px-4 py-3"
      data-testid="factory-flow-caption"
    >
      <span
        class="bg-primary text-primary-foreground mt-0.5 flex size-5 flex-none items-center justify-center rounded-full text-[11px] font-medium"
      >
        {{ stage + 1 }}
      </span>
      <div class="min-w-0">
        <p class="text-sm font-medium">
          {{ current.title }}
          <span class="text-muted-foreground font-normal">· {{ current.where }}</span>
        </p>
        <p class="text-muted-foreground text-xs">{{ current.body }}</p>
      </div>
      <div class="ml-auto flex flex-none gap-1 pt-1.5" role="group" aria-label="Stages">
        <button
          v-for="(s, index) in stages"
          :key="s.title"
          type="button"
          class="h-1.5 rounded-full transition-all"
          :class="
            index === stage
              ? 'bg-primary w-5'
              : 'bg-muted-foreground/30 hover:bg-muted-foreground/60 w-1.5'
          "
          :aria-label="`Show stage ${index + 1}: ${s.title}`"
          :aria-current="index === stage ? 'step' : undefined"
          @click="select(index)"
        />
      </div>
    </div>

    <ol class="sr-only">
      <li v-for="s in stages" :key="s.title">{{ s.title }} ({{ s.where }}): {{ s.body }}</li>
    </ol>
  </figure>
</template>

<style scoped>
.flow-lit {
  stroke-dasharray: 1;
  transition: stroke-dashoffset 0.9s cubic-bezier(0.65, 0, 0.35, 1);
}

.flow-return {
  opacity: 0;
  transition: opacity 0.4s;
}

.flow-return-active {
  opacity: 1;
  animation: flow-march 0.9s linear infinite;
}

@keyframes flow-march {
  to {
    stroke-dashoffset: -28;
  }
}

.flow-halo {
  opacity: 0;
  transition: opacity 0.5s;
}

.flow-node-active .flow-halo {
  opacity: 1;
  animation: flow-breathe 2.2s ease-in-out infinite;
  transform-box: fill-box;
  transform-origin: center;
}

@keyframes flow-breathe {
  50% {
    transform: scale(1.15);
  }
}

.flow-node circle,
.flow-node text {
  transition:
    stroke 0.4s,
    fill 0.4s;
}

.flow-packet {
  transition:
    transform 0.9s cubic-bezier(0.65, 0, 0.35, 1),
    opacity 0.3s;
}

.flow-packet-jump {
  transition: opacity 0.3s;
}

.flow-ping {
  transform-box: fill-box;
  transform-origin: center;
  animation: flow-ping 1.4s cubic-bezier(0, 0, 0.2, 1) infinite;
}

@keyframes flow-ping {
  75%,
  100% {
    transform: scale(2.6);
    opacity: 0;
  }
}
</style>
