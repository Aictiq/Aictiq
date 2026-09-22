# Usage telemetry

Aictiq does **not** send product telemetry by default. Telemetry is disabled unless the
operator explicitly sets `Telemetry:Enabled=true` and configures an HTTPS endpoint.

When enabled, Aictiq sends at most one small report per day. The payload contains only:

- the running Aictiq version;
- aggregate counts of active organizations, human accounts, and agent accounts; plus
  privacy-preserving Postgres table estimates for projects and work items. The estimates
  may lag recent writes because they do not bypass tenant row-level security.

It contains no installation ID, hostname, IP address supplied by Aictiq, user, organization,
project, item content, token, email address, configuration value, or error detail. The
endpoint naturally sees the network request metadata that any HTTP server receives; use an
endpoint you trust and your normal egress controls.

```dotenv
Telemetry__Enabled=true
Telemetry__Endpoint=https://telemetry.example.net/aictiq/v1/usage
```

The endpoint must be an absolute HTTPS URL. A missing or invalid endpoint means no report
is sent, even when `Telemetry:Enabled` is true. Failures are logged and never delay startup,
requests, migrations, or workers. Disable telemetry at any time with
`Telemetry__Enabled=false` and restart the API.

Telemetry is intentionally a lightweight adoption signal, not observability. Use the
[operations guide](/operations) and OpenTelemetry exporters for traces, metrics, and logs.
