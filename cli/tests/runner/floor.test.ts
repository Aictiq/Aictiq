import { describe, expect, it } from 'vitest'
import { Floor } from '../../src/runner/floor.js'

const settled = async (promise: Promise<unknown>) => {
  let done = false
  void promise.then(() => (done = true))
  await new Promise((resolve) => setTimeout(resolve, 5))
  return done
}

describe('Floor', () => {
  it('lets one organization run side by side and keeps the others out until it is done', async () => {
    const floor = new Floor(0)
    expect(floor.enter('acme')).toBe(true)
    expect(floor.enter('acme')).toBe(true)
    expect(floor.enter('globex')).toBe(false)
    expect(floor.current).toBe('acme')

    await floor.turn('acme')
    const globex = floor.turn('globex')
    floor.leave('acme')
    expect(await settled(globex)).toBe(false)
    floor.leave('acme')
    expect(await settled(globex)).toBe(true)
    expect(floor.current).toBeNull()
    expect(floor.enter('globex')).toBe(true)
  })

  it('makes the organization that held the floor stand back while another waits', async () => {
    const floor = new Floor(50)
    floor.enter('acme')
    const globex = floor.turn('globex')
    floor.leave('acme')

    const acme = floor.turn('acme')
    expect(await settled(globex)).toBe(true)
    expect(await settled(acme)).toBe(false)
    await acme

    // Alone, it does not wait at all.
    floor.enter('acme')
    floor.leave('acme')
    expect(await settled(floor.turn('acme'))).toBe(true)
  })

  it('stops waiting when the caller is stopping', async () => {
    const floor = new Floor()
    floor.enter('acme')
    const stop = new AbortController()
    const waiting = floor.turn('globex', stop.signal)
    expect(await settled(waiting)).toBe(false)
    stop.abort()
    expect(await settled(waiting)).toBe(true)
  })
})
