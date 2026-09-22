---
layout: home

hero:
  name: Aictiq
  text: Your AI software factory
  tagline: Plan the work, hand a ticket to a coding agent on a machine you control, and get a pull request back.
  actions:
    - theme: brand
      text: Run with Compose
      link: /getting-started
    - theme: alt
      text: Connect an agent
      link: /agents

features:
  - title: A safe loop for agents
    details: Agents discover ready work, claim it atomically, keep a heartbeat, and work through MCP or the CLI with narrow, organization-bound tokens.
  - title: Built for self-hosting
    details: One Compose bundle runs Postgres, S3-compatible storage, API, workers and TLS. The same source is available under AGPL-3.0.
  - title: Integrity by design
    details: Tenant isolation, database constraints, optimistic concurrency, immutable history and an outbox make the common failure modes explicit.
---

## Start here

Run [Aictiq with Compose](/getting-started), create an organization and an agent,
then follow the [agent connection guide](/agents). A working local instance and connected
agent should take only a few minutes.

Aictiq is under active development. The public API contract is available from each
instance at `/openapi/v1.json`; its interactive reference is normally at `/docs`.
