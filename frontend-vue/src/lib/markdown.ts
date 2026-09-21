import createDOMPurify from 'dompurify'
import MarkdownIt from 'markdown-it'

/**
 * Markdown rendering for descriptions, comments and wiki pages.
 *
 * **Sanitising is not optional here and not a formality.** The input is written by other
 * users — a teammate, a guest, or an AI agent acting on a prompt someone else wrote — and
 * it is rendered inside an authenticated page. `markdown-it` is configured with
 * `html: false`, which already refuses raw HTML, and the output is then run through
 * DOMPurify anyway: two independent controls, because the cost of the second one is a
 * millisecond and the cost of being wrong once is an account takeover.
 *
 * The API also stores a sanitised `description_html` server-side (see the data model);
 * this is the client half of the same rule, so a page rendering Markdown it fetched from
 * anywhere is safe.
 */

const md = new MarkdownIt({
  // No raw HTML. Markdown is a formatting language here, not a way to inject markup.
  html: false,
  linkify: true,
  breaks: true,
  typographer: false,
})

/**
 * GitHub-style task lists: `- [ ] todo` and `- [x] done`, the same Markdown the editor
 * writes. Written here rather than pulled in as a plugin because it is a dozen lines and a
 * renderer of other people's text is not where a dependency should be added lightly. The
 * checkbox is a token of its own, so its markup comes from this file and never from the
 * source text; sanitising then forces it disabled regardless.
 */
md.core.ruler.after('inline', 'task-lists', (state) => {
  const tokens = state.tokens
  for (let index = 2; index < tokens.length; index++) {
    const inline = tokens[index]!
    const first = inline.children?.[0]
    if (
      inline.type !== 'inline' ||
      tokens[index - 1]!.type !== 'paragraph_open' ||
      tokens[index - 2]!.type !== 'list_item_open' ||
      first?.type !== 'text'
    )
      continue
    const match = /^\[([ xX])\](?:\s+|$)/.exec(first.content)
    if (!match) continue

    const checked = match[1] !== ' '
    first.content = first.content.slice(match[0].length)
    const checkbox = new state.Token('task_checkbox', 'input', 0)
    checkbox.meta = { checked }
    inline.children!.unshift(checkbox)

    const item = tokens[index - 2]!
    item.attrJoin('class', checked ? 'task-list-item checked' : 'task-list-item')
    for (let back = index - 3; back >= 0; back--) {
      const token = tokens[back]!
      if (token.level === item.level - 1 && token.type.endsWith('_list_open')) {
        if (!String(token.attrGet('class') ?? '').includes('task-list'))
          token.attrJoin('class', 'task-list')
        break
      }
    }
  }
})
md.renderer.rules.task_checkbox = (tokens, idx) =>
  `<input type="checkbox" disabled${tokens[idx]!.meta?.checked ? ' checked' : ''}>`

/**
 * Links to anywhere else must not be able to reach back into this tab
 * (`window.opener`), and must not pass the current URL along as a referrer.
 */
const defaultLinkOpen =
  md.renderer.rules.link_open ??
  ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options))

md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
  const token = tokens[idx]!
  const href = String(token.attrGet('href') ?? '')

  // Relative links stay in-app; anything absolute opens out and is isolated.
  if (/^https?:\/\//i.test(href)) {
    token.attrSet('target', '_blank')
    token.attrSet('rel', 'noopener noreferrer nofollow')
  }

  return defaultLinkOpen(tokens, idx, options, env, self)
}

const purify = createDOMPurify(window)

/**
 * `target="_blank"` survives sanitising only alongside the `rel` above; the hook enforces
 * the pairing so a crafted link cannot arrive with one and not the other.
 */
purify.addHook('afterSanitizeAttributes', (node) => {
  if (node instanceof Element && node.tagName === 'A' && node.getAttribute('target') === '_blank') {
    node.setAttribute('rel', 'noopener noreferrer nofollow')
  }
  // The only input Markdown produces is a task-list checkbox, and it is read-only.
  if (node instanceof Element && node.tagName === 'INPUT') {
    node.setAttribute('type', 'checkbox')
    node.setAttribute('disabled', '')
  }
})

const ALLOWED_TAGS = [
  'p',
  'br',
  'hr',
  'strong',
  'em',
  'del',
  's',
  'code',
  'pre',
  'blockquote',
  'ul',
  'ol',
  'li',
  'a',
  'img',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
  'table',
  'thead',
  'tbody',
  'tr',
  'th',
  'td',
  'input', // task list checkboxes, forced disabled below
  'span',
]

const ALLOWED_ATTR = [
  'href',
  'src',
  'alt',
  'title',
  'target',
  'rel',
  'class',
  'type',
  'checked',
  'disabled',
]

/** Renders Markdown to sanitised HTML. The result is safe to bind with `v-html`. */
export function renderMarkdown(source: string): string {
  if (!source) return ''

  const html = md.render(source)

  return purify.sanitize(html, {
    ALLOWED_TAGS,
    ALLOWED_ATTR,
    // javascript:, data: and vbscript: hrefs never survive.
    ALLOWED_URI_REGEXP: /^(?:(?:https?|mailto):|[^a-z]|[a-z+.-]+(?:[^a-z+.\-:]|$))/i,
    FORBID_ATTR: ['style', 'onerror', 'onload', 'onclick'],
  })
}

/** Strips formatting for previews, search snippets and notification text. */
export function markdownToPlainText(source: string, maxLength = 200): string {
  const text = purify
    .sanitize(md.render(source), { ALLOWED_TAGS: [], ALLOWED_ATTR: [] })
    .replace(/\s+/g, ' ')
    .trim()

  return text.length > maxLength ? `${text.slice(0, maxLength - 1)}…` : text
}
