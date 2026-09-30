import { flushPromises, mount } from '@vue/test-utils'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'

import { useProjectRealtime } from '@/composables/useProjectRealtime'
import { activityToasterId } from '@/composables/useToast'
import { useSessionStore } from '@/stores/session'

/**
 * An item edit comes back through the hub to everyone looking at it, the person who made it
 * included. That echo used to land as "Item updated by <guid>" on top of the save's own
 * toast; now the editor hears only from the save, and everyone else hears a name.
 */
const toast = vi.hoisted(() => vi.fn())
vi.mock('vue-sonner', () => ({ toast: Object.assign(toast, { success: vi.fn(), error: vi.fn() }) }))

const handlers = vi.hoisted(() => new Map<string, (event: unknown) => void>())
vi.mock('@/utils/realtime', () => ({
  createHubConnection: () => ({
    state: 'Disconnected',
    on: (name: string, handler: (event: unknown) => void) => handlers.set(name, handler),
    onreconnected: () => {},
    start: async () => {},
    stop: async () => {},
    invoke: async () => {},
  }),
}))

const me = 'user-me'

function mountOn(itemKey: string, nameOf?: (actorId: string) => string | undefined) {
  const harness = defineComponent({
    setup() {
      useProjectRealtime(() => 'org', () => 'PROJ', () => itemKey, undefined, nameOf)
      return () => h('div')
    },
  })
  return mount(harness, { global: { plugins: [VueQueryPlugin] } })
}

function itemChanged(key: string, actorId: string) {
  handlers.get('item.changed')!({ id: 'i1', key, actorId, changedFields: ['title'] })
}

beforeEach(() => {
  toast.mockReset()
  handlers.clear()
  setActivePinia(createPinia())
  useSessionStore().user = { id: me } as never
})

describe('item.changed on the open item', () => {
  it('stays quiet about the viewer’s own save', async () => {
    mountOn('PROJ-1', () => 'Me Myself')
    await flushPromises()

    itemChanged('PROJ-1', me)
    expect(toast).not.toHaveBeenCalled()
  })

  it('names someone else, in the activity corner', async () => {
    mountOn('PROJ-1', (id) => (id === 'user-ana' ? 'Ana Horvat' : undefined))
    await flushPromises()

    itemChanged('PROJ-1', 'user-ana')
    expect(toast).toHaveBeenCalledWith('Ana Horvat updated this item.', {
      description: undefined,
      toasterId: activityToasterId,
    })
  })

  it('never shows a raw id for an actor it cannot name', async () => {
    mountOn('PROJ-1', () => undefined)
    await flushPromises()

    itemChanged('PROJ-1', '87432882-6ab4-44f8-a3da-0bebcd0f9a28')
    expect(toast).toHaveBeenCalledWith('Someone else updated this item.', expect.anything())
  })

  it('ignores changes to items other than the open one', async () => {
    mountOn('PROJ-1', () => 'Ana Horvat')
    await flushPromises()

    itemChanged('PROJ-2', 'user-ana')
    expect(toast).not.toHaveBeenCalled()
  })
})
