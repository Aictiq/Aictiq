import { describe, expect, it } from 'vitest'

import { isNewerRelease } from '@/api/meta'

describe('isNewerRelease', () => {
  it('compares stable semantic versions', () => {
    expect(isNewerRelease('0.1.0', '0.1.1')).toBe(true)
    expect(isNewerRelease('0.1.0', '0.2.0')).toBe(true)
    expect(isNewerRelease('1.0.0', '0.9.9')).toBe(false)
  })

  it('does not offer updates from dev or prerelease builds', () => {
    expect(isNewerRelease('0.1.0-dev', '0.1.0')).toBe(false)
    expect(isNewerRelease('0.1.0', '0.2.0-rc.1')).toBe(false)
    expect(isNewerRelease('not-a-version', '0.2.0')).toBe(false)
  })
})
