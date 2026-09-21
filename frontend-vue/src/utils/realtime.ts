import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'

import { CSRF_HEADER } from '@/utils/api'

/**
 * The one place a hub connection is built. Same-origin cookies authenticate it — no
 * query-string token is used — but SignalR's negotiate is a POST, so it is a
 * cookie-authenticated state-changing request like any other and needs
 * `X-Aictiq-Request`. `headers` reaches negotiate and the long-polling/SSE transports;
 * the WebSocket handshake is a GET and needs nothing.
 */
export function createHubConnection(url: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(url, { headers: { [CSRF_HEADER]: '1' } })
    .withAutomaticReconnect()
    .configureLogging(import.meta.env.DEV ? LogLevel.Warning : LogLevel.Error)
    .build()
}
