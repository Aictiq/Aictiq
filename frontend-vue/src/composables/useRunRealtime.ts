import { HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { onBeforeUnmount, onMounted, watch, type MaybeRefOrGetter, toValue } from 'vue'

import { createHubConnection } from '@/utils/realtime'
import { normalizeRunStatus } from '@/lib/runs'

export interface RunChangedEvent {
  runId: string
  itemId: string
  itemKey: string
  status?: string
  agentId?: string
  cancelRequested?: boolean
}

interface RunLogEvent {
  runId: string
  seq: number
  lines: string[]
}

/**
 * Follows one run: `JoinRun` for its `run:{id}` group (the live log - the audience the
 * log reads admit) and `JoinProject` for the project group, because `run.changed` - the
 * status and cancel flags - travels there, next to every other item change.
 *
 * Like `useProjectRealtime`, pushes are only hints: the log refetches through its
 * authorized endpoint and the run refetches its own detail. `JoinRun` is
 * operator-gated, so a non-operator's join fails with the same "not found" every
 * refusal uses; the project group keeps working and the page degrades quietly.
 */
export function useRunRealtime(options: {
  /** The organization the run belongs to - the page's own scope, not the remembered one. */
  organizationSlug: MaybeRefOrGetter<string | null>
  runId: MaybeRefOrGetter<string | undefined>
  projectKey: MaybeRefOrGetter<string | undefined>
  onLog?: () => void
  onChanged?: (event: RunChangedEvent) => void
}) {
  let connection: HubConnection | null = null

  async function join() {
    const runId = toValue(options.runId)
    const projectKey = toValue(options.projectKey)
    if (!runId || !projectKey) return
    const slug = toValue(options.organizationSlug)
    // A run page wants both groups, and one of them refusing must not take the other down.
    await Promise.allSettled([
      connection?.invoke('JoinRun', runId, slug),
      connection?.invoke('JoinProject', projectKey, slug),
    ])
  }

  async function start() {
    const runId = toValue(options.runId)
    if (!runId) return
    connection = createHubConnection('/hubs/projects')

    connection.on('run.log', (event: RunLogEvent) => {
      if (event.runId === runId) options.onLog?.()
    })
    connection.on('run.changed', (event: RunChangedEvent) => {
      if (event.runId !== runId) return
      // Realtime spells the timeout with an underscore; the DTO does not.
      options.onChanged?.({ ...event, status: normalizeRunStatus(event.status) ?? event.status })
    })

    connection.onreconnected(() => void join())
    await connection.start()
    await join()
  }

  async function stop() {
    const active = connection
    connection = null
    if (active && active.state !== HubConnectionState.Disconnected) await active.stop()
  }

  const warn = (error: unknown) => {
    if (import.meta.env.DEV) console.warn('[run realtime]', error)
  }

  onMounted(() => {
    start().catch(warn)
  })
  onBeforeUnmount(() => {
    stop().catch(warn)
  })
  watch(
    [
      () => toValue(options.organizationSlug),
      () => toValue(options.runId),
      () => toValue(options.projectKey),
    ],
    () => {
      stop()
        .then(start)
        .catch(warn)
    },
  )
}
