import { computed, ref } from 'vue'
import { useQuery, useQueryClient, type QueryKey } from '@tanstack/vue-query'
import { useRouter } from 'vue-router'

import {
  duplicateItem,
  editItem,
  transitionItem,
  type WorkItem,
  type WorkItemPriority,
} from '@/api/items'
import { getProject, hasProjectRole, listProjectMembers } from '@/api/projects'
import { listWorkflows } from '@/api/workflows'
import { projectKeyOf, useItemModal } from '@/composables/useItemModal'
import { useToast } from '@/composables/useToast'
import { allowedStates, replaceListedItem } from '@/lib/inline-edits'

/**
 * Quick edits made right on a list row - assignee, priority, state - plus copying a row's
 * link and duplicating it. `listKey` is the prefix of every cached query that shows the
 * rows (each page, filter and section), so a change shows on all of them at once.
 *
 * Saves are optimistic: the row changes on the spot, a row with a save in flight takes no
 * second edit, and a failure puts every cached list back as it was and says why. Success
 * marks the lists stale so their filters, sorting and totals catch up, and the board and
 * the item's own detail with them.
 */
export function useInlineItemEdits(options: {
  slug: () => string
  projectKey: () => string
  listKey: () => QueryKey
  /** Other queries a change moves, such as a sprint's totals beside the backlog. */
  related?: () => QueryKey[]
}) {
  const client = useQueryClient()
  const router = useRouter()
  const toast = useToast()
  const itemModal = useItemModal()
  const busy = ref(new Set<string>())

  // The same keys the board and the item detail use, so all three share one fetch.
  const members = useQuery({
    queryKey: computed(() => ['project-members', options.slug(), options.projectKey()]),
    queryFn: () => listProjectMembers(options.slug(), options.projectKey()),
  })
  const workflows = useQuery({
    queryKey: computed(() => [options.slug(), options.projectKey(), 'workflows']),
    queryFn: () => listWorkflows(options.slug(), options.projectKey()),
  })
  const project = useQuery({
    queryKey: computed(() => [options.slug(), options.projectKey(), 'project']),
    queryFn: () => getProject(options.slug(), options.projectKey()),
  })
  // Guests and archived projects read the same rows; the controls stay but cannot save.
  const canEdit = computed(
    () =>
      hasProjectRole(project.data.value?.role ?? 'guest', 'member') &&
      !project.data.value?.isArchived,
  )
  const memberList = computed(() => members.data.value ?? [])
  const workflow = computed(
    () => workflows.data.value?.find((entry) => entry.isDefault) ?? workflows.data.value?.[0],
  )
  const statesFor = (item: WorkItem) => allowedStates(workflow.value, item.stateId)
  const isBusy = (item: WorkItem) => busy.value.has(item.id)

  function setBusy(id: string, value: boolean) {
    const next = new Set(busy.value)
    if (value) next.add(id)
    else next.delete(id)
    busy.value = next
  }
  function refreshElsewhere(key: string) {
    return Promise.all([
      client.invalidateQueries({ queryKey: options.listKey() }),
      client.invalidateQueries({ queryKey: ['board', options.slug(), options.projectKey()] }),
      client.invalidateQueries({ queryKey: [options.slug(), options.projectKey(), 'items'] }),
      client.invalidateQueries({ queryKey: [options.slug(), key] }),
      ...(options.related?.() ?? []).map((queryKey) => client.invalidateQueries({ queryKey })),
    ])
  }

  async function save(
    item: WorkItem,
    optimistic: Partial<WorkItem>,
    request: () => Promise<WorkItem>,
    failure: string,
  ) {
    if (isBusy(item)) return
    setBusy(item.id, true)
    const filters = { queryKey: options.listKey() }
    await client.cancelQueries(filters)
    const snapshot = client.getQueriesData(filters)
    client.setQueriesData(filters, (data: unknown) =>
      replaceListedItem(data, item.id, (entry) => ({ ...entry, ...optimistic })),
    )
    try {
      const updated = await request()
      client.setQueriesData(filters, (data: unknown) =>
        replaceListedItem(data, item.id, () => updated),
      )
      client.setQueryData([options.slug(), item.key], updated)
      await refreshElsewhere(item.key)
    } catch (error) {
      for (const [key, data] of snapshot) client.setQueryData(key, data)
      toast.error(error, failure)
    } finally {
      setBusy(item.id, false)
    }
  }

  const changeAssignee = (item: WorkItem, assigneeId: string | null) =>
    save(
      item,
      { assigneeId },
      () => editItem(options.slug(), item, { assigneeId }),
      `The assignee of ${item.key} could not be changed.`,
    )
  const changePriority = (item: WorkItem, priority: WorkItemPriority) =>
    save(
      item,
      { priority },
      () => editItem(options.slug(), item, { priority }),
      `The priority of ${item.key} could not be changed.`,
    )
  // A transition, never a PATCH: it is the call that enforces the workflow's whitelist.
  function changeState(item: WorkItem, stateId: string) {
    const state = workflow.value?.states.find((entry) => entry.id === stateId)
    return save(
      item,
      { stateId, ...(state ? { stateCategory: state.category } : {}) },
      () => transitionItem(options.slug(), item.key, { toStateId: stateId, version: item.version }),
      `${item.key} could not be moved.`,
    )
  }

  function itemUrl(item: WorkItem) {
    const { href } = router.resolve({
      name: 'item-detail',
      params: {
        slug: options.slug(),
        projectKey: projectKeyOf(item.key) || options.projectKey(),
        itemKey: item.key,
      },
    })
    return new URL(href, window.location.origin).toString()
  }
  async function copyLink(item: WorkItem) {
    try {
      await navigator.clipboard.writeText(itemUrl(item))
      toast.success('Link copied')
    } catch (error) {
      toast.error(error, 'Could not copy to the clipboard.')
    }
  }

  async function duplicate(item: WorkItem) {
    if (isBusy(item)) return
    setBusy(item.id, true)
    try {
      const created = await duplicateItem(options.slug(), item.key)
      toast.success(`${created.key} created.`, `A copy of ${item.key}.`)
      await refreshElsewhere(created.key)
      itemModal.open(created.key)
    } catch (error) {
      toast.error(error, `${item.key} could not be duplicated.`)
    } finally {
      setBusy(item.id, false)
    }
  }

  return {
    canEdit,
    members: memberList,
    membersLoading: computed(() => members.isPending.value),
    statesFor,
    isBusy,
    changeAssignee,
    changePriority,
    changeState,
    copyLink,
    duplicate,
  }
}
