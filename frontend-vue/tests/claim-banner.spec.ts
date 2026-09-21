import { mount, RouterLinkStub } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import ClaimBanner from '@/components/common/ClaimBanner.vue'
import ClaimGlyph from '@/components/common/ClaimGlyph.vue'
import { canRelease, claimStatus, since, staleAfterMinutes } from '@/lib/claims'

/**
 * A claim is a lease, not an assignment, and the difference is invisible in every other
 * column on the screen. Three states have to stay apart: nobody holds this, someone is
 * working on it, and someone *was* working on it and has probably died — the last is the
 * only one where the useful action is to take the item back.
 */
const agent = { id: 'a1', displayName: 'claude-dev', avatarKey: null, isAgent: true }
const now = new Date('2026-09-07T12:00:00Z')
const minutesAgo = (n: number) => new Date(now.getTime() - n * 60_000).toISOString()

describe('claimStatus', () => {
  it('is none when nobody holds it', () => {
    expect(claimStatus({ claimedBy: null, claimHeartbeatAt: minutesAgo(1) }, now)).toBe('none')
  })

  it('is live while the heartbeat is fresh and stale once it is not', () => {
    expect(claimStatus({ claimedBy: 'a1', claimHeartbeatAt: minutesAgo(2) }, now)).toBe('live')
    expect(claimStatus({ claimedBy: 'a1', claimHeartbeatAt: minutesAgo(29) }, now)).toBe('live')
    expect(claimStatus({ claimedBy: 'a1', claimHeartbeatAt: minutesAgo(31) }, now)).toBe('stale')
  })

  it('treats a claim with no heartbeat as live rather than abandoned', () => {
    // The claim endpoint writes the first heartbeat in the same statement, so an absent
    // value means an old row — offering to break a claim on that basis would be wrong.
    expect(claimStatus({ claimedBy: 'a1', claimHeartbeatAt: null }, now)).toBe('live')
  })
})

describe('canRelease', () => {
  const holder = { claimedBy: 'a1', currentUserId: 'a1', projectRole: 'member' }

  it('lets the holder release their own claim, whatever their role', () => {
    expect(canRelease('live', holder)).toBe(true)
  })

  it('needs project admin to break someone else’s', () => {
    expect(canRelease('live', { ...holder, currentUserId: 'u2' })).toBe(false)
    expect(canRelease('stale', { ...holder, currentUserId: 'u2', projectRole: 'admin' })).toBe(true)
  })

  it('offers nothing when there is no claim', () => {
    expect(canRelease('none', { ...holder, projectRole: 'admin' })).toBe(false)
  })
})

describe('since', () => {
  it('reads as a duration, not a timestamp', () => {
    expect(since(minutesAgo(0), now)).toBe('just now')
    expect(since(minutesAgo(4), now)).toBe('4 min ago')
    expect(since(minutesAgo(180), now)).toBe('3 h ago')
    expect(since(minutesAgo(60 * 48), now)).toBe('2 d ago')
    expect(since(null, now)).toBe('')
  })
})

describe('ClaimBanner', () => {
  const render = (props: Record<string, unknown>) =>
    mount(ClaimBanner, {
      props: { claimedBy: 'a1', holder: agent, ...props },
      global: { stubs: { RouterLink: RouterLinkStub } },
    })

  it('renders nothing at all when nobody holds the item', () => {
    expect(render({ claimedBy: null }).find('[role="status"]').exists()).toBe(false)
  })

  it('names the holder and shows the heartbeat while the claim is live', () => {
    const text = render({ claimHeartbeatAt: new Date().toISOString() }).text()

    expect(text).toContain('claude-dev')
    expect(text).toContain('is working on this')
    expect(text).not.toContain('has not checked in')
  })

  it('warns, and says when anyone may take it back, once the claim goes stale', () => {
    const text = render({
      claimHeartbeatAt: new Date(Date.now() - 90 * 60_000).toISOString(),
    }).text()

    expect(text).toContain('has not checked in')
    expect(text).toContain(String(staleAfterMinutes))
  })

  it('offers Release only to someone allowed to take it', () => {
    const stranger = render({ currentUserId: 'u2', projectRole: 'member' })
    expect(stranger.text()).not.toContain('Release')

    const admin = render({ currentUserId: 'u2', projectRole: 'admin' })
    expect(admin.text()).toContain('Release')
  })

  it('emits release rather than calling the API itself', async () => {
    const wrapper = render({ currentUserId: 'a1' })
    await wrapper.find('button').trigger('click')

    expect(wrapper.emitted('release')).toHaveLength(1)
  })

  describe('when the claim belongs to a live run', () => {
    const liveRun = { runnerName: 'vps-1', startedAt: new Date().toISOString() }

    it('says whose machine has been on it since when, and offers the log', () => {
      const wrapper = render({
        claimHeartbeatAt: new Date(Date.now() - 90 * 60_000).toISOString(),
        liveRun,
        runLogTo: '/o/acme/factory/runs/r-1',
      })

      expect(wrapper.text()).toContain('is running on vps-1')
      expect(wrapper.text()).toContain('started just now')
      // A runner heartbeats its run, not the claim, so the claim's own staleness is
      // meaningless here — the healthy-run text wins over the stale warning.
      expect(wrapper.text()).not.toContain('has not checked in')
      expect(wrapper.findComponent(RouterLinkStub).props('to')).toBe('/o/acme/factory/runs/r-1')
    })

    it('says a queued run is waiting, not running', () => {
      const wrapper = render({ liveRun: { status: 'queued', runnerName: null, startedAt: null } })

      expect(wrapper.text()).toContain('has a run queued')
      expect(wrapper.text()).not.toContain('is running')
    })

    it('offers no Release: a run ends by being cancelled, not by breaking its claim', () => {
      const wrapper = render({ currentUserId: 'u2', projectRole: 'admin', liveRun })

      expect(wrapper.text()).not.toContain('Release')
    })

    it('shows no log link to someone who cannot operate the factory', () => {
      const wrapper = render({ liveRun, runLogTo: null })

      expect(wrapper.text()).toContain('is running on vps-1')
      expect(wrapper.findComponent(RouterLinkStub).exists()).toBe(false)
    })
  })
})

describe('ClaimGlyph', () => {
  it('says nothing about an unclaimed row', () => {
    expect(mount(ClaimGlyph, { props: { claimedBy: null } }).find('span').exists()).toBe(false)
  })

  it('carries the whole story in its label, since the glyph itself is one icon', () => {
    const live = mount(ClaimGlyph, {
      props: { claimedBy: 'a1', holderName: 'claude-dev', claimHeartbeatAt: new Date().toISOString() },
    })
    expect(live.attributes('aria-label')).toContain('claude-dev is working on this')

    const stale = mount(ClaimGlyph, {
      props: {
        claimedBy: 'a1',
        holderName: 'claude-dev',
        claimHeartbeatAt: new Date(Date.now() - 120 * 60_000).toISOString(),
      },
    })
    expect(stale.attributes('aria-label')).toContain('has not checked in')
  })
})
