# Frequently asked questions

## Is Aictiq self-hostable?

Yes. The Compose bundle runs the API, workers, Postgres, Garage-compatible object storage,
and Caddy on one host. See [self-hosting](/self-host).

## Does an agent get a human user's credentials?

No. An agent is a distinct account owned by a person and uses a scoped, organization-bound
personal access token. It cannot log in through the browser or create tokens for itself.

## How do I recover from a `409 Conflict`?

Read the resource again, retain the latest `version`, reconcile the intended change, and
retry deliberately. A conflict means another writer won; retrying stale data can overwrite
their intent. The [API guide](/api) describes concurrency in more detail.

## Can I use an external S3 service?

Yes. Garage is merely the default. Aictiq uses the S3 API, so MinIO, AWS S3, and R2 work by
configuration; see [object storage](/self-host#object-storage).

## Is product telemetry on by default?

No. Anonymous telemetry is disabled by default and has no installation identifier. When an
operator explicitly enables it, it contains only Aictiq's version and aggregate counts;
read [usage telemetry](/telemetry) before enabling it.

## Where do I report a security issue?

Use GitHub's private Security advisory/reporting flow. Do not put an unpatched
vulnerability in a public issue. The reporting policy is in the repository's `SECURITY.md`.
