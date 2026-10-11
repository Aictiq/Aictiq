import { describe, expect, it } from 'vitest'
import { CliError, ExitCode } from '../../src/errors.js'
import { collectTags, InvalidTagsMessage, normalizeTags } from '../../src/runner/tags.js'

describe('runner tags', () => {
  it('collects a repeated --tag and comma-separated lists into one array', () => {
    expect(['gpu', 'docker,Linux'].reduce((all, value) => collectTags(value, all), [] as string[])).toEqual([
      'gpu',
      'docker',
      'Linux',
    ])
  })

  it('trims, lower-cases, drops duplicates and blanks, and sorts', () => {
    expect(normalizeTags([' GPU ', 'docker', 'gpu', '', 'zone:eu-1', 'a.b_c'])).toEqual([
      'a.b_c',
      'docker',
      'gpu',
      'zone:eu-1',
    ])
    expect(normalizeTags(undefined)).toEqual([])
  })

  it.each([
    [['-gpu']],
    [['has space']],
    [['emoji-✓']],
    [['x'.repeat(51)]],
    [Array.from({ length: 21 }, (_, i) => `t${i}`)],
  ])('refuses %j as a usage error', (raw) => {
    const error = (() => {
      try {
        normalizeTags(raw)
      } catch (e) {
        return e
      }
    })()
    expect(error).toBeInstanceOf(CliError)
    expect((error as CliError).exitCode).toBe(ExitCode.Validation)
    expect((error as CliError).message).toBe(InvalidTagsMessage)
  })

  it('accepts the limits themselves', () => {
    expect(normalizeTags(['x'.repeat(50)])).toHaveLength(1)
    // Twenty distinct after normalization: duplicates do not count against the limit.
    expect(normalizeTags([...Array.from({ length: 20 }, (_, i) => `t${i}`), 'T0'])).toHaveLength(20)
  })
})
