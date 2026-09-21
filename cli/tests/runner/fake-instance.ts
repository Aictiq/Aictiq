import { createServer } from 'node:http'
import type { IncomingMessage, Server, ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'

export interface Recorded {
  method: string
  path: string
  query: Record<string, string>
  authorization: string | undefined
  body: unknown
}

export type Handler = (request: Recorded) => { status: number; body?: unknown } | undefined

/**
 * A stand-in for the instance's runner protocol: a real HTTP server on an ephemeral port,
 * so the client, its headers and its error handling are exercised as they are in production.
 * Unhandled routes answer 204.
 */
export class FakeInstance {
  readonly requests: Recorded[] = []
  private readonly handlers: Handler[] = []
  private server: Server | undefined
  url = ''

  on(handler: Handler): this {
    this.handlers.push(handler)
    return this
  }

  async start(): Promise<this> {
    this.server = createServer((req, res) => void this.handle(req, res))
    await new Promise<void>((resolve) => this.server!.listen(0, '127.0.0.1', resolve))
    this.url = `http://127.0.0.1:${(this.server.address() as AddressInfo).port}`
    return this
  }

  async stop(): Promise<void> {
    this.server?.closeAllConnections()
    await new Promise<void>((resolve) => this.server?.close(() => resolve()) ?? resolve())
  }

  to(pathSuffix: string): Recorded[] {
    return this.requests.filter((r) => r.path.endsWith(pathSuffix))
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const chunks: Buffer[] = []
    for await (const chunk of req) chunks.push(chunk as Buffer)
    const text = Buffer.concat(chunks).toString('utf8')
    const url = new URL(req.url ?? '/', 'http://x')
    const recorded: Recorded = {
      method: req.method ?? '',
      path: url.pathname,
      query: Object.fromEntries(url.searchParams),
      authorization: req.headers.authorization,
      body: text ? JSON.parse(text) : undefined,
    }
    this.requests.push(recorded)
    for (const handler of this.handlers) {
      const answer = handler(recorded)
      if (!answer) continue
      if (answer.body instanceof Uint8Array) {
        res.writeHead(answer.status, { 'Content-Type': 'application/octet-stream' })
        res.end(answer.body)
        return
      }
      res.writeHead(answer.status, { 'Content-Type': 'application/json' })
      res.end(answer.body === undefined ? '' : JSON.stringify(answer.body))
      return
    }
    res.writeHead(204)
    res.end()
  }
}
