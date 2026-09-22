import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'

import {
  fuzzyScore,
  parsePaletteMode,
  useCommands,
  useCommandStore,
  type Command,
} from '@/composables/useCommands'

const command = (over: Partial<Command> = {}): Command => ({
  id: 'test',
  label: 'Test command',
  group: 'Test',
  run: () => {},
  ...over,
})

beforeEach(() => setActivePinia(createPinia()))

describe('fuzzyScore', () => {
  it('matches a subsequence, which is how people actually type into a palette', () => {
    expect(fuzzyScore('New item', 'nit')).not.toBeNull()
    expect(fuzzyScore('New item', 'ni')).not.toBeNull()
  })

  it('rejects letters that are not there at all', () => {
    expect(fuzzyScore('New item', 'xyz')).toBeNull()
  })

  it('ranks a contiguous prefix above a scattered match', () => {
    const prefix = fuzzyScore('Board', 'boa')!
    const scattered = fuzzyScore('Backlog of archived', 'boa')!

    expect(prefix).toBeLessThan(scattered)
  })

  it('treats an empty query as matching everything equally', () => {
    expect(fuzzyScore('anything', '')).toBe(0)
  })
})

describe('palette modes', () => {
  it('parses every contextual prefix without leaking it into the query term', () => {
    expect(parsePaletteMode('> new')).toEqual({ mode: 'commands', term: 'new' })
    expect(parsePaletteMode('g items')).toEqual({ mode: 'go', term: 'items' })
    expect(parsePaletteMode('@alice')).toEqual({ mode: 'assign', term: 'alice' })
    expect(parsePaletteMode('#done')).toEqual({ mode: 'state', term: 'done' })
    expect(parsePaletteMode('~bug')).toEqual({ mode: 'label', term: 'bug' })
  })
})

describe('command store', () => {
  it('groups results in the order the groups first appear', () => {
    const store = useCommandStore()
    store.register(
      command({ id: 'a', label: 'Alpha', group: 'Navigation' }),
      command({ id: 'b', label: 'Beta', group: 'Appearance' }),
      command({ id: 'c', label: 'Gamma', group: 'Navigation' }),
    )

    expect(store.results.map((g) => g.label)).toEqual(['Navigation', 'Appearance'])
    expect(store.results[0]!.items.map((c) => c.id)).toEqual(['a', 'c'])
  })

  it('filters to matches as the query narrows', () => {
    const store = useCommandStore()
    store.register(
      command({ id: 'board', label: 'Go to Board', group: 'Navigation' }),
      command({ id: 'theme', label: 'Toggle theme', group: 'Appearance' }),
    )

    store.query = 'board'
    expect(store.flatResults.map((c) => c.id)).toEqual(['board'])
  })

  it('matches on keywords that are not in the label', () => {
    const store = useCommandStore()
    store.register(
      command({ id: 'logout', label: 'Log out', group: 'Account', keywords: 'sign out' }),
    )

    store.query = 'sign'
    expect(store.flatResults.map((c) => c.id)).toEqual(['logout'])
  })

  it('hides a command that cannot run - offering it would be a dead end', () => {
    const store = useCommandStore()
    store.register(
      command({ id: 'always', label: 'Always', group: 'Test' }),
      command({ id: 'never', label: 'Never', group: 'Test', when: () => false }),
    )

    expect(store.available.map((c) => c.id)).toEqual(['always'])
  })

  it('replaces a command registered twice under the same id', () => {
    const store = useCommandStore()
    store.register(command({ id: 'dup', label: 'First' }))
    store.register(command({ id: 'dup', label: 'Second' }))

    expect(store.commands).toHaveLength(1)
    expect(store.commands[0]!.label).toBe('Second')
  })

  it('unregisters commands so an unmounted page stops offering them', () => {
    const store = useCommandStore()
    store.register(command({ id: 'gone' }))
    store.unregister('gone')

    expect(store.commands).toHaveLength(0)
  })

  it('gives a page its command back when the surface stacked over it goes away', () => {
    const store = useCommandStore()
    const page = command({ id: 'items.start-run', label: 'Hand the page item to an agent' })
    const peek = command({ id: 'items.start-run', label: 'Hand the peeked item to an agent' })
    store.register(page)
    store.register(peek)
    expect(store.commands.map((c) => c.label)).toEqual(['Hand the peeked item to an agent'])

    // The peek closes: its registration goes, and only its own.
    store.release(peek)
    expect(store.commands.map((c) => c.label)).toEqual(['Hand the page item to an agent'])
  })

  it('releases a component\'s commands when it unmounts', () => {
    const store = useCommandStore()
    const Surface = defineComponent({
      setup() {
        useCommands(() => [command({ id: 'surface' })])
        return () => h('div')
      },
    })

    const first = mount(Surface)
    const second = mount(Surface)
    first.unmount()
    expect(store.commands.map((c) => c.id)).toEqual(['surface'])
    second.unmount()
    expect(store.commands).toHaveLength(0)
  })

  it('closes before running, so an action that navigates does not leave it open', async () => {
    const store = useCommandStore()
    let openWhileRunning: boolean | null = null

    const target = command({
      id: 'nav',
      run: () => {
        openWhileRunning = store.open
      },
    })

    store.register(target)
    store.show()
    await store.run(target)

    expect(openWhileRunning).toBe(false)
    expect(store.open).toBe(false)
  })

  it('clears the query each time it opens', () => {
    const store = useCommandStore()
    store.query = 'stale'
    store.show()

    expect(store.query).toBe('')
  })

  it('awaits an async command', async () => {
    const store = useCommandStore()
    const run = vi.fn(async () => {})
    const target = command({ id: 'async', run })

    await store.run(target)
    expect(run).toHaveBeenCalledOnce()
  })
})
