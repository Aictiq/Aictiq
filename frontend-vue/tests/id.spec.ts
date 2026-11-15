import { afterEach, describe, expect, it, vi } from 'vitest'
import { newId } from '@/utils/id'

describe('client UUIDs', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('uses the native UUID API when available', () => {
    const randomUUID = vi.fn(() => '26da51d2-e39b-4e13-81e8-96503ab51faa')
    const getRandomValues = vi.fn()
    vi.stubGlobal('crypto', { randomUUID, getRandomValues })

    expect(newId()).toBe('26da51d2-e39b-4e13-81e8-96503ab51faa')
    expect(randomUUID).toHaveBeenCalledOnce()
    expect(getRandomValues).not.toHaveBeenCalled()
  })

  it.each([0x00, 0xff])(
    'sets v4 and variant bits and pads bytes without randomUUID (%i)',
    (value) => {
      const getRandomValues = vi.fn((bytes: Uint8Array) => bytes.fill(value))
      vi.stubGlobal('crypto', { randomUUID: undefined, getRandomValues })

      expect(newId()).toBe(
        value === 0
          ? '00000000-0000-4000-8000-000000000000'
          : 'ffffffff-ffff-4fff-bfff-ffffffffffff',
      )
      expect(getRandomValues).toHaveBeenCalledOnce()
      expect(getRandomValues.mock.calls[0]![0]).toHaveLength(16)
    },
  )

  it('draws fresh random bytes for each fallback UUID', () => {
    const getRandomValues = crypto.getRandomValues.bind(crypto)
    vi.stubGlobal('crypto', { getRandomValues })
    const ids = Array.from({ length: 100 }, () => newId())

    for (const id of ids)
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(new Set(ids).size).toBe(ids.length)
  })
})
