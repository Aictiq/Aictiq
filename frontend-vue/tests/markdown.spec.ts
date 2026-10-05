// @vitest-environment jsdom
//
// Not happy-dom: DOMPurify silently no-ops against it - `ALLOWED_TAGS: []` returns the
// input unchanged - so every assertion below would pass without sanitising anything. A
// security test that cannot fail is worse than no test, hence jsdom for this file only.

import { describe, expect, it } from 'vitest'

import { markdownToPlainText, referencedItemKeys, renderMarkdown } from '@/lib/markdown'

/**
 * The Markdown renderer takes text written by other people - teammates, guests, and AI
 * agents acting on prompts someone else supplied - and puts it inside an authenticated
 * page. These are the assertions that keep that safe.
 */
describe('renderMarkdown', () => {
  it('renders ordinary Markdown', () => {
    const html = renderMarkdown('# Title\n\nSome **bold** text.')

    expect(html).toContain('<h1>Title</h1>')
    expect(html).toContain('<strong>bold</strong>')
  })

  it('refuses raw HTML rather than rendering it', () => {
    const html = renderMarkdown('<img src=x onerror="alert(1)">')

    // markdown-it escapes it, so the characters survive as visible text - what must not
    // survive is a real element the browser would act on.
    expect(html).not.toContain('<img')
    expect(html).toContain('&lt;img')
  })

  it('strips a script tag', () => {
    const html = renderMarkdown('Hello <script>alert(1)</script> world')

    expect(html.toLowerCase()).not.toContain('<script')
  })

  it('drops a javascript: link', () => {
    // The classic: valid Markdown link syntax, hostile href. The text may remain; the
    // anchor must not.
    const html = renderMarkdown('[click me](javascript:alert(1))')

    expect(html).not.toContain('href="javascript:')
    expect(html).not.toContain('<a ')
  })

  it('never emits an event-handler attribute, however the text is crafted', () => {
    const html = renderMarkdown(
      [
        '[x](https://example.com "onmouseover=alert(1)")',
        '<b onclick="alert(1)">y</b>',
        '<div onload=alert(1)>z</div>',
      ].join('\n\n'),
    )

    // Parsed, not string-matched: the same characters are harmless inside a title or as
    // escaped text, and dangerous only as a real attribute. Only the DOM can tell those
    // apart, so the assertion asks the DOM.
    const root = document.createElement('div')
    root.innerHTML = html

    const handlers = [...root.querySelectorAll('*')].flatMap((element) =>
      [...element.attributes]
        .map((attribute) => attribute.name)
        .filter((name) => name.startsWith('on')),
    )

    expect(handlers).toEqual([])
  })

  it('isolates external links from the tab that opened them', () => {
    const html = renderMarkdown('[example](https://example.com)')

    expect(html).toContain('target="_blank"')
    // Without noopener the opened page can navigate this one via window.opener.
    expect(html).toContain('noopener')
    expect(html).toContain('noreferrer')
  })

  it('leaves relative links in-app', () => {
    const html = renderMarkdown('[an item](/items/ACME-1)')

    expect(html).toContain('href="/items/ACME-1"')
    expect(html).not.toContain('target="_blank"')
  })

  it('renders fenced code without executing anything', () => {
    const html = renderMarkdown('```ts\nconst a = 1\n```')

    expect(html).toContain('<pre>')
    expect(html).toContain('const a = 1')
  })

  it('renders task lists as read-only checkboxes', () => {
    const root = document.createElement('div')
    root.innerHTML = renderMarkdown('## Criteria\n\n- [ ] open\n- [x] done\n- plain')

    const items = [...root.querySelectorAll('li')]
    const boxes = items.map((item) => item.querySelector('input'))
    expect(root.querySelector('ul')?.classList.contains('task-list')).toBe(true)
    expect(items.map((item) => item.textContent)).toEqual(['open', 'done', 'plain'])
    expect(boxes.map((box) => box?.checked ?? null)).toEqual([false, true, null])
    expect(boxes.slice(0, 2).every((box) => box?.disabled && box.type === 'checkbox')).toBe(true)
    expect(items[1]!.classList.contains('checked')).toBe(true)
  })

  it('leaves brackets that are not a task marker alone', () => {
    const html = renderMarkdown('[ ] not in a list\n\n- [link](/x) in a list\n- [y] not a mark')

    expect(html).not.toContain('<input')
    expect(html).toContain('[ ] not in a list')
    expect(html).toContain('[y] not a mark')
  })

  it('renders an empty source as an empty string', () => {
    expect(renderMarkdown('')).toBe('')
  })
})

describe('markdownToPlainText', () => {
  it('strips formatting for previews and notifications', () => {
    expect(markdownToPlainText('# Title\n\nSome **bold** text.')).toBe('Title Some bold text.')
  })

  it('truncates with an ellipsis', () => {
    const text = markdownToPlainText('a'.repeat(300), 20)

    expect(text).toHaveLength(20)
    expect(text.endsWith('…')).toBe(true)
  })
})

describe('ticket references', () => {
  const context = { slug: 'acme', projectKey: 'ACME', itemKeys: ['ACME-12', 'OTHER-1'] }

  it('links only resolved tickets from the current project', () => {
    const root = document.createElement('div')
    root.innerHTML = renderMarkdown('See #ACME-12 (#ACME-12). #ACME-999 #OTHER-1', context)
    expect([...root.querySelectorAll('a')].map((link) => [link.textContent, link.getAttribute('href')])).toEqual([
      ['#ACME-12', '/o/acme/p/ACME/items/ACME-12'],
      ['#ACME-12', '/o/acme/p/ACME/items/ACME-12'],
    ])
    expect(root.textContent).toContain('#ACME-999 #OTHER-1')
    expect(renderMarkdown('#ACME-12')).not.toContain('<a')
  })

  it('ignores code, links, URL anchors, malformed keys and headings', () => {
    const source = ['`#ACME-12`', '```md\n#ACME-12\n```',
      '[#ACME-12](/example)', 'https://example.com/#ACME-12', 'word#ACME-12 #ACME-12suffix #ACME-12-3', '# Heading'].join('\n\n')
    const root = document.createElement('div')
    root.innerHTML = renderMarkdown(source, context)
    expect(root.querySelectorAll('a[href="/o/acme/p/ACME/items/ACME-12"]')).toHaveLength(0)
    expect(referencedItemKeys(source, 'ACME')).toEqual([])
  })

  it('extracts unique visible project keys to resolve before rendering', () => {
    expect(referencedItemKeys('**#ACME-12** (#ACME-12) #ACME-999 #OTHER-1', 'ACME')).toEqual(['ACME-12', 'ACME-999'])
  })
})
