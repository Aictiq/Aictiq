import { Command } from 'commander'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js'
import { Server } from '@modelcontextprotocol/sdk/server/index.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
import {
  CallToolRequestSchema,
  GetPromptRequestSchema,
  ListPromptsRequestSchema,
  ListResourceTemplatesRequestSchema,
  ListResourcesRequestSchema,
  ListToolsRequestSchema,
  ReadResourceRequestSchema,
} from '@modelcontextprotocol/sdk/types.js'
import { resolveSettings } from '../context.js'
import { version } from '../version.js'
import type { GlobalOptions } from '../context.js'
import { CliError, ExitCode } from '../errors.js'

/**
 * `aictiq mcp` is a stdio MCP server that forwards to the instance's Streamable HTTP
 * `/mcp` endpoint. Clients that only speak stdio (or that should not be handed a bearer
 * token in their configuration file) point at this instead: the token comes from the
 * CLI's own 0600 config, so an agent's `.mcp.json` can be committed.
 *
 * The bridge deliberately holds no catalogue of its own - every list and call is
 * forwarded, so a tool added to the API on Tuesday is reachable through an unchanged CLI
 * on Wednesday. Only `notifications/initialized` and the handshake are handled locally.
 */
export function mcpCommand(globals: () => GlobalOptions): Command {
  return new Command('mcp')
    .description('Run a stdio MCP server bridged to this instance (for Claude Code and friends)')
    .action(async () => {
      const settings = resolveSettings(globals())
      if (!settings.url || !settings.token) {
        throw new CliError(
          'No instance configured. Run `aictiq auth login --url <url>` or set AICTIQ_URL and AICTIQ_TOKEN.',
          ExitCode.Auth,
        )
      }

      // The bridge itself offers no sampling, roots or elicitation: it forwards server
      // capabilities downstream and nothing upstream.
      const upstream = new Client({ name: 'aictiq-cli', version }, { capabilities: {} })
      try {
        await upstream.connect(
          new StreamableHTTPClientTransport(
            new URL('/mcp', `${settings.url.replace(/\/+$/, '')}/`),
            { requestInit: { headers: { Authorization: `Bearer ${settings.token}` } } },
          ),
        )
      } catch (cause) {
        const detail = cause instanceof Error ? cause.message : String(cause)
        // `/mcp` needs more than a valid token: the `mcp` scope, and a token bound to one
        // organization. Both refusals arrive here as a bare 403, which on its own tells
        // nobody which of the two to fix.
        throw new CliError(
          detail.includes('403') || detail.includes('401')
            ? `${settings.url} refused the token for /mcp. It needs the "mcp" scope and must be bound to one organization - create it under Settings → Access tokens with an organization selected.`
            : `Cannot open an MCP session at ${settings.url}/mcp: ${detail}`,
          ExitCode.Auth,
        )
      }

      // Advertise exactly what the instance advertises: a client that sees `prompts` here
      // but gets "method not found" from upstream has been lied to by the bridge.
      const capabilities = upstream.getServerCapabilities() ?? {}
      const server = new Server(
        { name: 'aictiq', version },
        {
          capabilities: {
            ...(capabilities.tools ? { tools: capabilities.tools } : {}),
            ...(capabilities.resources ? { resources: capabilities.resources } : {}),
            ...(capabilities.prompts ? { prompts: capabilities.prompts } : {}),
          },
        },
      )

      if (capabilities.tools) {
        server.setRequestHandler(ListToolsRequestSchema, (request) =>
          upstream.listTools(request.params),
        )
        server.setRequestHandler(CallToolRequestSchema, (request) =>
          upstream.callTool(request.params),
        )
      }
      if (capabilities.resources) {
        server.setRequestHandler(ListResourcesRequestSchema, (request) =>
          upstream.listResources(request.params),
        )
        server.setRequestHandler(ListResourceTemplatesRequestSchema, (request) =>
          upstream.listResourceTemplates(request.params),
        )
        server.setRequestHandler(ReadResourceRequestSchema, (request) =>
          upstream.readResource(request.params),
        )
      }
      if (capabilities.prompts) {
        server.setRequestHandler(ListPromptsRequestSchema, (request) =>
          upstream.listPrompts(request.params),
        )
        server.setRequestHandler(GetPromptRequestSchema, (request) =>
          upstream.getPrompt(request.params),
        )
      }

      // stdout is the transport, so nothing else may be written to it while the bridge
      // runs; diagnostics go to stderr.
      await server.connect(new StdioServerTransport())

      const shutdown = async () => {
        await Promise.allSettled([server.close(), upstream.close()])
        process.exit(0)
      }
      process.on('SIGINT', () => void shutdown())
      process.on('SIGTERM', () => void shutdown())

      // Resolve only when stdin closes, which is how a stdio MCP client says goodbye.
      await new Promise<void>((resolve) => process.stdin.once('close', resolve))
      await shutdown()
    })
}
