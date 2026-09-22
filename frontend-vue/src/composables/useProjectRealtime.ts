import { HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { onBeforeUnmount, onMounted, watch, type MaybeRefOrGetter, toValue } from 'vue'
import { useQueryClient } from '@tanstack/vue-query'

import { useToast } from '@/composables/useToast'
import { createHubConnection } from '@/utils/realtime'

interface ItemChanged { id: string; key: string; actorId: string; changedFields: string[] }
interface CommentAdded { itemId: string; commentId: string; actorId: string }
interface RunChanged {
  runId: string
  itemId: string
  itemKey: string
  status?: string
  agentId?: string
  cancelRequested?: boolean
}

/**
 * Keeps a project view fresh without trusting pushes as a data source. Hub messages are
 * deliberately only invalidations; each affected screen refetches through its normal,
 * authorized HTTP endpoint and retains its usual optimistic-concurrency behaviour.
 */
/**
 * @param organizationSlug Which organization's project this page is showing. It is passed
 * in rather than read from the store because a project key is unique per organization,
 * not per instance: the page's own scope is the truth, and someone who is in two
 * organizations that both have a `WEB` must not subscribe to the other one's.
 */
export function useProjectRealtime(
  organizationSlug: MaybeRefOrGetter<string | null>,
  projectKey: MaybeRefOrGetter<string>,
  openItemKey?: MaybeRefOrGetter<string | undefined>,
  onBoardMoved?: (event: ItemChanged) => void,
) {
  const client = useQueryClient()
  const toast = useToast()
  let connection: HubConnection | null = null

  function invalidate(kind: 'items' | 'item' | 'board' | 'sprint', itemKey?: string) {
    void client.invalidateQueries({
      predicate: (query) => query.queryKey.some((part) =>
        typeof part === 'string' && (part === kind || part === 'items' || part === 'board' || part === 'sprint' || part === itemKey)),
    })
  }

  async function start() {
    const key = toValue(projectKey)
    if (!key) return
    connection = createHubConnection('/hubs/projects')

    connection.on('item.changed', (event: ItemChanged) => {
      invalidate('items', event.key)
      if (toValue(openItemKey) === event.key) toast.info(`Item updated by ${event.actorId}`)
    })
    connection.on('comment.added', (event: CommentAdded) => {
      invalidate('item', event.itemId)
      if (toValue(openItemKey)) void client.invalidateQueries({ queryKey: [toValue(openItemKey), 'comments'] })
    })
    connection.on('board.moved', (event: ItemChanged) => { onBoardMoved?.(event); invalidate('board', event.key) })
    connection.on('sprint.changed', () => invalidate('sprint'))
    // A dispatch claims the item for its agent and a finish releases it, so the item and
    // its lists go stale with every run change; the run's own screens refetch too.
    connection.on('run.changed', (event: RunChanged) => {
      invalidate('items', event.itemKey)
      void client.invalidateQueries({
        predicate: (query) => query.queryKey.includes('runs') || query.queryKey.includes(event.runId),
      })
    })
    // A project key is unique per organization, not per instance: the slug says which
    // organization's project this page is showing.
    const join = () => connection?.invoke('JoinProject', toValue(projectKey), toValue(organizationSlug))
    connection.onreconnected(() => join())
    await connection.start()
    await join()
  }

  async function stop() {
    const active = connection
    connection = null
    if (active && active.state !== HubConnectionState.Disconnected) await active.stop()
  }

  // Realtime is an enhancement over polling-free refetches, never the data source, so a
  // hub that will not connect degrades to "screens refresh when they ask" rather than
  // taking the page down with an unhandled rejection.
  const warn = (error: unknown) => { if (import.meta.env.DEV) console.warn('[realtime]', error) }

  onMounted(() => { start().catch(warn) })
  onBeforeUnmount(() => { stop().catch(warn) })
  watch(
    () => [toValue(organizationSlug), toValue(projectKey)] as const,
    () => { stop().then(start).catch(warn) },
  )
}
