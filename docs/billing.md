# Billing (SaaS only)

Self-hosted Aictiq has no billing: `Billing:Mode` defaults to `self_hosted`, every
organization is unlimited, the Plan page shows usage only, no part of the product is
gated, there is no upgrade call to action anywhere, and nothing here needs configuring.
Retention and safety limits an operator configures - `Retention:*`, `Automation:*`,
`Analytics:RetentionDays`, the rate limits - keep working exactly as configured; feature
parity is not a promise to disable them. This page is for running the hosted service.

The hosted offer is one flat plan: **USD $49 per organization per month**
(plan code `hosted`) - unlimited humans, agent identities, teams, projects and work
items, all implemented core features. New hosted organizations start a 30-day
evaluation: no card, no automatic charge at expiry, explicit checkout to become paid.
A founding price of $29/month for the first 12 paid billing periods can be granted
server-side to the pilot cohort. There are no seat charges and no overage charges.

## Allowances

Hosted evaluation and paid Hosted organizations get the same allowances. They are
service allowances, not a smaller product: nothing on the list below unlocks a feature.

| Allowance | Value | What it does not touch |
| --- | --- | --- |
| Committed attachments | 10 GiB per organization | Pending and deleted attachments do not count. Over the line, reads, downloads and exports keep working; deleting attachments frees space. |
| Finished-run raw logs | 90 days | Run records, outcome summaries, failure reasons, prompt snapshots, playbook revision references, linked pull requests and item history are kept. |
| Analytics history | 365 days | A **report window**, enforced in queries. Source work-item history and audit records are never deleted to enforce it. |

Two things follow from that and are worth stating plainly:

- **Expired run logs cannot be recovered by purchasing a subscription later.** The chunks
  are deleted; buying Hosted afterwards does not bring them back. Everything else about
  those runs survives.
- The **8 MiB per-run log cap** (`Automation:MaxLogBytes`) is unchanged and independent of
  retention; a capped log is visibly marked truncated. Runner compute and model usage are
  supplied and paid for by the customer, and Aictiq adds no token markup.

Automation and Analytics learn these numbers through `IPlanAllowances`
(`SharedKernel/Contracts`), which Billing implements: run-log retention days and
analytics history days, per organization. Neither module reads a billing table. A null
answer means "no entitlement opinion" and the module keeps its operator-configured
value - which is the self-hosted answer, and every legacy plan's.

Billing is still Stripe subscriptions: Stripe Checkout starts a subscription,
the Customer Portal handles payment methods, invoices and cancellation, and Stripe
webhooks keep Aictiq's copy (`billing.subscriptions`) current. The browser never talks
to Stripe directly - the API returns a Checkout or Portal URL and the page navigates to
it - so there is no Stripe script, no publishable key and no CSP change.

## Configuration

| Key | Environment | Notes |
| --- | --- | --- |
| `Billing:Mode` | `Billing__Mode` | `saas` to bill. Anything else bills nothing, whatever keys are set. |
| `Stripe:SecretKey` | `Stripe__SecretKey` | `sk_test_…` / `sk_live_…`. Secret. |
| `Stripe:WebhookSecret` | `Stripe__WebhookSecret` | `whsec_…` of the webhook endpoint. Secret. |
| `Stripe:Prices:hosted_organization` | `Stripe__Prices__hosted_organization` | Price id (`price_…`) of the $49/month organization subscription, recurring monthly, quantity 1. Required to sell Hosted. |
| `Stripe:Prices:hosted_founding` | `Stripe__Prices__hosted_founding` | Price id of the founding price ($29/month). Optional; selling the founding offer needs it and `Billing:FoundingPrice`. |
| `Stripe:Prices:<plan>_human` / `<plan>_agent` | `Stripe__Prices__starter_human` | Legacy per-human and extra-agent prices. Still read for subscriptions created before the flat plan; never sold to anyone new. |
| `Billing:EvaluationDays` | `Billing__EvaluationDays` | Default 30. Length of the hosted evaluation. |
| `Billing:FoundingPrice` | `Billing__FoundingPrice` | USD amount of the founding price. Unset - the default - means the offer is not running on this instance. |
| `Billing:FoundingPeriods` | `Billing__FoundingPeriods` | Default 12. Discounted monthly billing periods before the plan's own price returns. |
| `Billing:StorageAllowanceBytes` | `Billing__StorageAllowanceBytes` | Optional operational cap on committed attachments for this deployment. It only ever **narrows** the plan's allowance - the smaller of the two wins - and null defers to the plan (10 GiB on Hosted). |
| `Billing:GracePeriodDays` | | Default 14. How long an organization keeps writing after a failed payment. |
| `Billing:SeatSyncInterval` | | Default `1.00:00:00`. The nightly billing reconciliation (seat re-derivation for legacy subscriptions; founding-transition safety net). |
| `Billing:StripeEventRetentionDays` | | Default 30. How long processed Stripe event ids are kept for de-duplication. |

Both the API and Workers need the Stripe keys and prices: the API creates sessions and takes
webhooks, Workers run the nightly reconciliation. Nothing is validated on start - without
both keys a SaaS instance boots, the billing endpoints answer `409 billing-unavailable`,
and `/webhooks/stripe` answers 404.

Price keys are `Stripe:Prices:{plan}_{kind}`. The kinds are `organization` (the flat
hosted line), `founding` (the same plan's discounted price) and the legacy `human` /
`agent` seat kinds, which only the retired plans' surviving subscriptions use. Hosted is
*sold* when `hosted_organization` has a price configured.

### Local development (Aspire)

Secrets are Aspire parameters, declared only when they have a value, so a stack without them
runs exactly as before. In the AppHost's user secrets:

```bash
cd backend/src/AppHost
dotnet user-secrets set "Parameters:stripe-secret-key" "sk_test_..."
dotnet user-secrets set "Parameters:stripe-webhook-secret" "whsec_..."   # from `stripe listen`, below
dotnet user-secrets set "Billing:Mode" "saas"
dotnet user-secrets set "Stripe:Prices:hosted_organization" "price_..."
dotnet user-secrets set "Stripe:Prices:hosted_founding" "price_..."
dotnet user-secrets set "Billing:FoundingPrice" "29"
```

## Stripe test mode, end to end

1. In the Stripe dashboard (test mode) create the hosted product with one recurring
   monthly price at $49 per organization (quantity 1), plus - if you are selling the
   founding offer - a second recurring monthly price at $29. Put the price ids in the
   configuration above.
2. Configure the Customer Portal (Settings → Billing → Customer portal): allow updating the
   payment method, viewing invoices and cancelling. **Do not allow plan switching there** -
   plan changes go through Aictiq's checkout, so the offer rules and entitlement checks
   apply. A plan changed in the Stripe dashboard is still applied when its webhook
   arrives; limits then refuse new over-allowance writes but remove nothing.
3. Forward webhooks to the API (not through Vite - `/webhooks` is not proxied):
   ```bash
   stripe listen --forward-to http://localhost:<api-port>/webhooks/stripe \
     --events checkout.session.completed,customer.subscription.created,customer.subscription.updated,customer.subscription.deleted,invoice.payment_failed,invoice.payment_succeeded
   ```
   It prints the `whsec_…` signing secret to use as `Parameters:stripe-webhook-secret`.
   In production, create a webhook endpoint at `https://<host>/webhooks/stripe` with the
   same events.
4. Sign up and create an organization - the evaluation starts with it. Open
   *Settings → Billing* as its Owner and check out for Hosted. Pay with
   `4242 4242 4242 4242`. After the redirect the page says Stripe has the payment;
   within a few seconds the organization's plan is `hosted`.
5. Payment failure: `stripe trigger invoice.payment_failed` against a test customer, or
   attach card `4000 0000 0000 0341` and advance a test clock. The shell shows the grace
   banner; after `GracePeriodDays` project writes answer `409 org-read-only`.

## How it works

- **Endpoints.** `GET /orgs/{slug}/billing/subscription` (any member - it drives the
  banner) returns the evaluation (`startedAt`, `endsAt`, `expired`), the founding terms
  when the subscription is on them (`price`, `periods`, `periodsBilled`,
  `renewalPrice`, `convertedAt`) and the plan's `organizationPrice`.
  `POST …/billing/checkout {plan}` and `POST …/billing/portal` (Owner, `admin` scope).
  Checkout accepts `hosted`, or `free` to cancel; the legacy plan codes are refused for
  new subscriptions. With no subscription, checkout returns a Stripe Checkout URL; with
  one, it changes the subscription in place with proration (or, for `free`, cancels at
  period end) and the webhook confirms it.
- **Evaluation.** When a hosted organization is created, Billing receives the
  `OrganizationCreated` outbox event and starts one evaluation, persisted in
  `billing.evaluations`. `ux_evaluations_organization_id` - a unique index on the
  organization - is what makes "exactly one" a database fact, so a redelivered event or
  two racing deliveries cannot mint a second window, and `ck_evaluations_window`
  (`ends_at > started_at`) refuses a degenerate one. Nothing writes to the row after the
  day it is born: restarts, invitations, new members and plan requests never move it. There is no trial-to-paid
  conversion: at expiry the organization becomes read-only through the same mechanism
  as a failed payment - reads, downloads, exports, the Portal and checkout keep working,
  while ordinary mutations, new agent dispatches and rule-triggered runs stop; runs
  still queued are cancelled and in-flight runs finish within their existing deadlines,
  including outcome writes and credential cleanup. Explicit checkout is required to
  become paid.
- **Founding offer.** Granted server-side to the pilot cohort; the browser cannot grant
  itself the discount. Each `invoice.payment_succeeded` on the founding price counts a
  discounted period; after `Billing:FoundingPeriods` the Stripe subscription is switched
  to the standard price automatically, with the nightly reconciliation as the safety
  net. Entitlement never changes - the renewal is Hosted, not a different plan.
- **Flat charge.** A hosted subscription is one organization at quantity one. Membership
  changes never touch it, and the nightly reconciliation NEVER changes a hosted
  subscription's charge: how many humans or agents belong to the organization is
  irrelevant to what it pays. (Seat re-derivation still runs for legacy subscriptions.)
- **Over-allowance.** Committing an attachment that would exceed the committed storage
  allowance answers `402 plan-limit` with `limit: "storage_bytes"` and a message that
  explains deleting attachments frees space - not an upgrade to a nonexistent higher
  tier. Reads, downloads and exports keep working when over.
- **Legacy plans.** `free`, `starter`, `team` and `enterprise` are closed to new
  subscriptions and retained for existing ones. A subscription already on one keeps
  working unchanged - its per-seat quantities are still re-derived nightly and its
  legacy `{plan}_human` / `{plan}_agent` price keys are still read. Nothing is silently
  migrated, repriced, given a fresh evaluation, or deleted; moving to Hosted is an
  explicit checkout by the organization's Owner.
  `free` is the exception that is still reachable, because it is where cancelling lands
  an organization - it is a cancellation target, not an offer. It is not sold, not
  marketed, and it still carries its old caps; nothing anywhere should present it as a
  permanent hosted free tier.
- **Webhooks** (`POST /webhooks/stripe`) are anonymous, signature-verified with Stripe's
  `EventUtility` before anything is parsed, exempt from the per-IP rate limiter (Stripe sends
  from few addresses) and outside the CSRF rule (no cookie). Each event id is inserted into
  `billing.stripe_events` with `ON CONFLICT DO NOTHING` in the same transaction as its
  effect, so a redelivery is a 200 no-op. Events are applied only if newer (Stripe's
  `created`) than the last one applied, and a cancelled subscription is never revived.
- **Plan.** Tenancy owns `organizations.plan`. Billing raises `OrganizationBillingChanged`
  through the outbox; Tenancy's handler asks Billing for the entitled plan and stores it, so
  replays and reordering converge on the newest state.
- **Grace and read-only.** The first `invoice.payment_failed` starts a grace period anchored
  to Stripe's timestamp (retries do not extend it). When it ends the organization is
  read-only through `RequireProjectWritable` - reads, the Portal and checkout keep working -
  until a `customer.subscription.updated` shows the subscription active again. Evaluation
  expiry uses the same read-only mechanism.

## Operating it

- Watch Workers logs for nightly billing reconciliation failures and for outbox dead
  letters of `OrganizationBillingChanged` / `OrganizationMemberAdded` (see
  `operations.md`). For a hosted organization neither is a charge problem - membership
  cannot change the bill - but it means Aictiq's copy of the subscription state may be
  stale.
- A webhook Stripe reports as failing with 400 is a signature problem - almost always a
  rotated or mistyped `Stripe:WebhookSecret`.
- `billing.subscriptions` is Aictiq's copy; Stripe is the truth. To re-sync one organization
  after fixing something by hand in Stripe, trigger any subscription update there (the
  webhook applies it) or wait for the nightly run.

## Legacy customers at the time of the change

**There are none.** Aictiq is pre-launch: no hosted instance has ever sold a
subscription, and this repository contains no production billing data - `billing.plans`
is seeded, and `billing.subscriptions`, `billing.evaluations` and `billing.stripe_events`
are empty on every environment that exists (development, CI and the test containers,
which create a fresh database per test class).

The migration to the flat offer asked for an inventory of existing hosted organizations
and subscriptions. That inventory is this paragraph: the cohort is empty, so there is no
migration to perform and none was invented. The legacy plan rows stay in the schema
because the code and the historical tests still reference them, not because anyone is
on them.

If that ever stops being true - a hosted instance sells a subscription - run the
inventory for real before changing plan rows or Stripe prices, and treat the migration,
the entitlement change and the Stripe price change as three separate, separately
reversible operations.
