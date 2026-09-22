import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js'

const [url, token] = process.argv.slice(2)
if (!url || !token) throw new Error('Usage: mcp-conformance.mjs <base-url> <pat>')

const client = new Client({ name: 'aictiq-compose-conformance', version: '1' }, { capabilities: {} })
await client.connect(new StreamableHTTPClientTransport(new URL('/mcp', `${url.replace(/\/+$/, '')}/`), {
  requestInit: { headers: { Authorization: `Bearer ${token}` } },
}))

try {
  // listTools resolves to a ListToolsResult - { tools, nextCursor? } - not an array.
  const { tools } = await client.listTools()
  for (const name of ['whoami', 'list_projects', 'list_ready_work']) {
    if (!tools.some((tool) => tool.name === name)) throw new Error(`MCP discovery omitted ${name}`)
  }
  const whoami = await client.callTool({ name: 'whoami' })
  if (whoami.isError) throw new Error('MCP whoami returned a tool error')
  console.log(`MCP SDK conformance passed (${tools.length} tools discovered)`)
} finally {
  await client.close()
}
