import { defineStore } from 'pinia'
import { computed, getCurrentScope, onScopeDispose, ref, shallowRef } from 'vue'

/**
 * The command registry behind the palette (⌘K).
 *
 * A registry rather than a hard-coded list because the palette is meant to be the fastest
 * route to *any* action, and the actions live all over the app: a board knows how to move
 * an item, an item page knows how to assign one. Each surface registers what it can do
 * while it is mounted and unregisters on the way out, so the palette only ever offers
 * things that would actually work.
 */

export interface Command {
  id: string
  label: string
  /** Grouping header in the palette. */
  group: string
  /** A `useShortcut` binding string, shown as a hint. Registering it is separate. */
  shortcut?: string
  /** Single glyph or short mono string, matching the design's icon column. */
  icon?: string
  /** Extra words to match on that are not in the label ("sign out" for "Log out"). */
  keywords?: string
  /** Hidden when this returns false - a command that cannot run must not be offered. */
  when?: () => boolean
  run: () => void | Promise<void>
}

export type PaletteMode = 'search' | 'commands' | 'go' | 'assign' | 'state' | 'label'

/** Prefixes are deliberately parsed in one pure helper so every palette surface agrees. */
export function parsePaletteMode(query: string): { mode: PaletteMode; term: string } {
  const trimmed = query.trimStart()
  const prefix: Record<string, PaletteMode> = { '>': 'commands', g: 'go', '@': 'assign', '#': 'state', '~': 'label' }
  const mode = prefix[trimmed[0] ?? '']
  return mode ? { mode, term: trimmed.slice(1).trimStart() } : { mode: 'search', term: trimmed }
}

/**
 * Subsequence match, the behaviour people expect from a palette: "nit" finds "New item".
 * Returns a score (lower is better) or null. Contiguous runs and matches at word starts
 * score better, so exact-ish typing beats a lucky scatter of letters.
 */
export function fuzzyScore(haystack: string, needle: string): number | null {
  if (needle.length === 0) return 0

  const text = haystack.toLowerCase()
  const query = needle.toLowerCase()

  let score = 0
  let from = 0
  let previousIndex = -1

  for (const character of query) {
    const index = text.indexOf(character, from)
    if (index === -1) return null

    if (previousIndex >= 0 && index === previousIndex + 1) {
      score += 1 // contiguous
    } else if (index === 0 || ' -/_.'.includes(text[index - 1] ?? '')) {
      score += 2 // start of a word
    } else {
      score += 6 + (index - from)
    }

    previousIndex = index
    from = index + 1
  }

  // Prefer shorter labels when the match quality is otherwise equal.
  return score + text.length * 0.05
}

export const useCommandStore = defineStore('commands', () => {
  /**
   * Every live registration, oldest first. Two mounted surfaces can register the same id -
   * an item page and the item peeked over it - so the newest one is what the palette
   * offers, and the page's comes back when the peek closes rather than vanishing with it.
   */
  const registrations = shallowRef<Command[]>([])
  const open = ref(false)
  const query = ref('')

  const commands = computed(() => {
    const byId = new Map<string, Command>()
    for (const command of registrations.value) {
      byId.delete(command.id)
      byId.set(command.id, command)
    }
    return [...byId.values()]
  })

  function register(...toAdd: Command[]) {
    registrations.value = [...registrations.value, ...toAdd]
  }

  /** Removes these exact registrations, leaving another surface's commands of the same id. */
  function release(...toRemove: Command[]) {
    const remove = new Set(toRemove)
    registrations.value = registrations.value.filter((c) => !remove.has(c))
  }

  function unregister(...ids: string[]) {
    const remove = new Set(ids)
    registrations.value = registrations.value.filter((c) => !remove.has(c.id))
  }

  const available = computed(() => commands.value.filter((c) => !c.when || c.when()))

  /** Ranked matches for the current query, grouped in the order groups first appear. */
  const results = computed(() => {
    const scored = available.value
      .map((command) => ({
        command,
        score: fuzzyScore(`${command.label} ${command.keywords ?? ''}`.trim(), query.value.trim().replace(/^>\s*/, '')),
      }))
      .filter((entry): entry is { command: Command; score: number } => entry.score !== null)
      .sort((a, b) => a.score - b.score)

    const groups = new Map<string, Command[]>()
    for (const { command } of scored) {
      const bucket = groups.get(command.group)
      if (bucket) bucket.push(command)
      else groups.set(command.group, [command])
    }

    return [...groups].map(([label, items]) => ({ label, items }))
  })

  const flatResults = computed(() => results.value.flatMap((group) => group.items))

  function show() {
    query.value = ''
    open.value = true
  }

  function hide() {
    open.value = false
  }

  function toggle() {
    if (open.value) hide()
    else show()
  }

  async function run(command: Command) {
    // Close first: an action that navigates should not leave the palette hanging over
    // the page it moved to.
    hide()
    await command.run()
  }

  return {
    commands,
    open,
    query,
    available,
    results,
    flatResults,
    register,
    release,
    unregister,
    show,
    hide,
    toggle,
    run,
  }
})

/**
 * Registers commands for the lifetime of the calling component. The unregister on
 * teardown is the point - a stale command pointing at an unmounted page is worse than a
 * missing one.
 */
export function useCommands(commands: () => Command[]) {
  const store = useCommandStore()

  const registered = commands()
  store.register(...registered)

  const dispose = () => store.release(...registered)
  // Every view mounts its own shell, so a registration that outlived its component would
  // pile up on each navigation; releasing with the component's scope is what keeps it one.
  if (getCurrentScope()) onScopeDispose(dispose)

  return { store, dispose }
}
