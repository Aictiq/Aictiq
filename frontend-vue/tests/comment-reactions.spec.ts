import { flushPromises, mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import CommentReactions from '@/components/items/CommentReactions.vue'
import { commentReactionOptions, type WorkItemComment } from '@/api/comments'

const api = vi.hoisted(() => ({
  reactToComment: vi.fn(),
  unreactFromComment: vi.fn(),
  saveFailed: vi.fn(),
}))
vi.mock('@/api/comments', async (original) => ({
  ...(await original<object>()),
  reactToComment: api.reactToComment,
  unreactFromComment: api.unreactFromComment,
}))
vi.mock('@/composables/useToast', () => ({ useToast: () => ({ saveFailed: api.saveFailed }) }))

const user = { id: 'ana', displayName: 'Ana Kovač', avatarKey: null, isAgent: false }
let wrapper: ReturnType<typeof mount>
let client: QueryClient
const comment = (reacted = false): WorkItemComment => ({
  id: 'comment1',
  canReact: true,
  author: user,
  bodyMarkdown: 'Ready',
  bodyHtml: '<p>Ready</p>',
  createdAt: '2026-10-01T10:00:00Z',
  editedAt: null,
  deletedAt: null,
  mentions: [],
  revisions: [],
  parentCommentId: null,
  reactions: [{ emoji: '👍', count: 1, reactedByMe: reacted, users: [user] }],
})
function render(canReact = true, reacted = false) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  wrapper = mount(CommentReactions, {
    props: { slug: 'org', itemKey: 'THR-1', comment: comment(reacted), canReact },
    global: { plugins: [[VueQueryPlugin, { queryClient: client }]] },
  })
  return wrapper
}
beforeEach(() => {
  vi.resetAllMocks()
  api.reactToComment.mockResolvedValue(undefined)
  api.unreactFromComment.mockResolvedValue(undefined)
})
afterEach(() => {
  wrapper?.unmount()
  client?.clear()
})

describe('comment reactions', () => {
  it('offers all six reactions and sends the selected emoji', async () => {
    render()
    await wrapper.get('[aria-label="Add reaction"]').trigger('click')
    for (const option of commentReactionOptions)
      expect(wrapper.find(`[aria-label="${option.label}"]`).exists()).toBe(true)
    await wrapper.get('[aria-label="Heart"]').trigger('click')
    await flushPromises()
    expect(api.reactToComment).toHaveBeenCalledWith('org', 'THR-1', 'comment1', '❤️')
    expect(wrapper.find('[aria-label="Choose a reaction"]').exists()).toBe(false)
  })
  it('shows names, counts and the current user highlight, and removes their reaction', async () => {
    render(true, true)
    const chip = wrapper.get('[aria-label="Thumbs up: 1, you reacted"]')
    expect(chip.attributes('title')).toBe('Ana Kovač')
    expect(chip.attributes('aria-pressed')).toBe('true')
    expect(chip.classes()).toContain('bg-primary/10')
    await chip.trigger('click')
    await flushPromises()
    expect(api.unreactFromComment).toHaveBeenCalledWith('org', 'THR-1', 'comment1', '👍')
  })
  it('keeps counts visible for read-only viewers and hides the picker', async () => {
    render(false)
    expect(wrapper.find('[aria-label="Add reaction"]').exists()).toBe(false)
    const chip = wrapper.get('[aria-label="Thumbs up: 1"]')
    expect(chip.attributes('disabled')).toBeDefined()
    await chip.trigger('click')
    expect(api.reactToComment).not.toHaveBeenCalled()
  })
  it('refreshes the authorized comments response after saving and reports failures', async () => {
    render()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    await wrapper.get('[aria-label="Thumbs up: 1"]').trigger('click')
    await flushPromises()
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['org', 'THR-1', 'comments'] })
    api.reactToComment.mockRejectedValueOnce(new Error('Forbidden'))
    await wrapper.get('[aria-label="Thumbs up: 1"]').trigger('click')
    await flushPromises()
    expect(api.saveFailed).toHaveBeenCalledWith(
      expect.any(Error),
      'Your reaction could not be saved.',
    )
    expect(wrapper.get('[aria-label="Add reaction"]').attributes('disabled')).toBeUndefined()
  })
})
