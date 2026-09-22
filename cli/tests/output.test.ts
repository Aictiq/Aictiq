import { describe, expect, it } from 'vitest'
import { renderFields, renderTable } from '../src/output.js'

describe('renderTable', () => {
  it('pads columns and leaves the last one unpadded so grep and cut still work', () => {
    const rows = [
      { key: 'ACME-1', title: 'Short' },
      { key: 'ACME-1000', title: 'A much longer title' },
    ]
    expect(
      renderTable(rows, [
        { header: 'KEY', value: (r) => r.key },
        { header: 'TITLE', value: (r) => r.title },
      ]),
    ).toBe(['KEY        TITLE', 'ACME-1     Short', 'ACME-1000  A much longer title'].join('\n'))
  })

  it('right-aligns the columns that ask for it', () => {
    expect(
      renderTable([{ n: 1 }, { n: 100 }], [
        { header: 'N', value: (r) => String(r.n), align: 'right' },
        { header: 'X', value: () => 'x' },
      ]),
    ).toBe(['  N  X', '  1  x', '100  x'].join('\n'))
  })

  it('prints just the header when there are no rows', () => {
    expect(renderTable([], [{ header: 'KEY', value: () => '' }])).toBe('KEY')
  })
})

describe('renderFields', () => {
  it('drops empty values instead of printing blank lines', () => {
    expect(
      renderFields([
        ['Type', 'task'],
        ['Assignee', ''],
        ['Claimed by', undefined],
        ['Version', '7'],
      ]),
    ).toBe(['Type     task', 'Version  7'].join('\n'))
  })
})
