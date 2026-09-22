/**
 * The configuration snippets the "Connect an agent" drawer hands out.
 *
 * They live here rather than in the component because they are the actual product of that
 * screen - a person pastes them into a file and expects them to work - and because the
 * rules they encode are worth asserting directly: the URL is *this* deployment's, and the
 * stdio form must never carry the token, which is the whole reason to prefer it.
 */

/** Shown when no token was just issued. Aictiq never shows a secret twice. */
export const tokenPlaceholder = 'aiq_your_agent_token'

export interface AgentSnippetContext {
  /** The app's origin. `/mcp` is same-origin: the API serves the SPA. */
  origin: string
  /** The secret from a token created moments ago, or null on a later visit. */
  secret?: string | null
  /** A project key to make the CLAUDE.md block concrete, when one is obvious. */
  projectKey?: string
}

export const mcpUrl = (origin: string) => `${origin.replace(/\/+$/, '')}/mcp`

/** Claude Code's `.mcp.json` talking to `/mcp` directly. The token is in the file. */
export function httpMcpSnippet({ origin, secret }: AgentSnippetContext): string {
  return JSON.stringify(
    {
      mcpServers: {
        aictiq: {
          type: 'http',
          url: mcpUrl(origin),
          headers: { Authorization: `Bearer ${secret ?? tokenPlaceholder}` },
        },
      },
    },
    null,
    2,
  )
}

/**
 * The same thing through the CLI's stdio bridge. It deliberately takes no token: the CLI
 * reads one from `~/.config/aictiq/config.json` (mode 0600), so this file holds no secret
 * and can be committed.
 */
export function stdioMcpSnippet(): string {
  return JSON.stringify({ mcpServers: { aictiq: { command: 'aictiq', args: ['mcp'] } } }, null, 2)
}

export function cliLoginSnippet({ origin }: AgentSnippetContext): string {
  return `npm install -g @aictiq/cli\naictiq auth login --url ${origin.replace(/\/+$/, '')}`
}

/** The loop, as a block for a repository's own CLAUDE.md. Mirrors `docs/agents.md`. */
export function claudeMdSnippet({ projectKey }: AgentSnippetContext): string {
  const key = projectKey ?? 'ACME'
  return `## Work tracking

Work is tracked in Aictiq, reachable through the \`aictiq\` MCP server.

- Start with \`whoami\`, then \`list_ready_work(project: "${key}")\`.
- **Claim before you write code**: \`claim_item(key, version)\`. A 409 means someone else
  got there first - pick another item, do not retry.
- Read \`get_item(key)\` in full: it carries the parent chain, the comments and the links.
- Branch \`${key.toLowerCase()}-123-short-slug\` and start commit subjects with
  \`${key}-123: \` so commits attach to the item.
- Report progress by editing one comment marked \`<!-- aictiq:progress -->\`, not by adding
  a comment per step.
- \`link_item\` the pull request, then \`transition_item\` to "In Review".
- \`release_item\` if you stop before finishing.`
}
