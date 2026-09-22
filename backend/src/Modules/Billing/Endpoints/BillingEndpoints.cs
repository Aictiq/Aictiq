using System.Text;
using Aictiq.Modules.Billing.Domain;
using Aictiq.Modules.Billing.Payments;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing.Endpoints;

public sealed record CheckoutRequest(string? Plan);

/// <param name="Url">Where to send the browser: Stripe Checkout or the Customer Portal. Null when the change was made in place.</param>
/// <param name="Changed">True when an existing subscription was changed without leaving Aictiq; the webhook confirms it.</param>
public sealed record BillingRedirectView(string? Url, bool Changed);

public sealed record SeatsView(int Human, int Agent);

public sealed record PlanOptionView(
    string Code, decimal HumanSeatPrice, decimal? OrganizationPrice, int? IncludedAgentsPerHuman,
    PlanLimits Limits, bool Purchasable);

/// <param name="Expired">Computed against the server's clock; the browser's is not trusted with this.</param>
public sealed record EvaluationView(DateTimeOffset StartedAt, DateTimeOffset EndsAt, bool Expired);

/// <summary>
/// The founding offer as the Plan page tells it: what it costs, how long it lasts, how
/// much of it has been used, and the price it returns to. The entitlement never changes -
/// this is a price, not a tier.
/// </summary>
/// <param name="EndsAt">
/// When the discounted price stops, computed here rather than in the browser: the current
/// period's end plus the periods still to be collected. Null until Stripe has reported a
/// period end, and null once the offer has already converted.
/// </param>
public sealed record FoundingOfferView(
    decimal Price, int Periods, int PeriodsBilled, decimal RenewalPrice, DateTimeOffset? ConvertedAt,
    DateTimeOffset? EndsAt);

/// <param name="Plan">The plan limits are enforced against right now.</param>
/// <param name="SubscribedPlan">The plan the Stripe subscription is for; null without one.</param>
/// <param name="Enabled">Whether this instance takes payments at all - hide every billing action when false.</param>
/// <param name="ReadOnly">Writes are refused: an evaluation has expired, or a grace period after a failed payment has ended.</param>
public sealed record SubscriptionView(
    string Mode,
    bool Enabled,
    string Plan,
    string? SubscribedPlan,
    SubscriptionStatus Status,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    SeatsView Seats,
    DateTimeOffset? PaymentFailedAt,
    DateTimeOffset? GraceEndsAt,
    bool ReadOnly,
    bool HasBillingAccount,
    EvaluationView? Evaluation,
    FoundingOfferView? Founding,
    IReadOnlyList<PlanOptionView> Plans);

public static class BillingEndpoints
{
    /// <summary>Stripe events are a few kilobytes; nothing legitimate comes close to this.</summary>
    private const long MaxWebhookBytes = 512 * 1024;

    public static IEndpointRouteBuilder MapBillingApiEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/billing").WithTags("Billing").RequireAuthorization();
        group.MapGet("/", Summary).RequireOrgRole(OrgRole.Owner).RequireScope(Scopes.Admin);
        // Every member reads this: the payment-failed banner is for everyone who is about to
        // lose the ability to write, not only for the Owner who can fix it. It carries no
        // Stripe identifiers and nothing a member could not already infer.
        group.MapGet("/subscription", GetSubscription).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        group.MapPost("/checkout", Checkout).RequireOrgRole(OrgRole.Owner).RequireScope(Scopes.Admin);
        group.MapPost("/portal", Portal).RequireOrgRole(OrgRole.Owner).RequireScope(Scopes.Admin);
        return api;
    }

    /// <summary>
    /// Stripe's webhook URL, outside the versioned prefix like GitHub's. Anonymous - the
    /// signature is the credential - and exempt from rate limiting: the global limiter
    /// partitions by IP, and Stripe delivers from a handful of addresses, so a burst of
    /// legitimate events (a nightly run correcting many subscriptions) would otherwise be
    /// throttled into retries. An unsigned flood costs one HMAC per request and no query.
    /// CSRF does not apply: the middleware guards cookie-authenticated requests, and Stripe
    /// sends no cookie.
    /// </summary>
    public static IEndpointRouteBuilder MapStripeWebhookEndpoint(this IEndpointRouteBuilder root)
    {
        root.MapPost("/webhooks/stripe", ReceiveWebhook)
            .WithTags("Billing")
            .AllowAnonymous()
            .DisableRateLimiting()
            .ExcludeFromDescription();
        return root;
    }

    private static async Task<IResult> Summary(ICurrentTenant tenant, BillingDbContext db, BillingUsage usage,
        IOrganizationBillingState state, IOptions<BillingOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        var facts = await usage.GetAsync(organizationId, includeStorage: true, ct);
        if (facts is null) return Results.NotFound();
        var plan = await BillingPlans.FindAsync(db, await BillingPlans.EffectiveCodeAsync(
            state, organizationId, facts.StoredPlan, !options.Value.IsSaas, ct), ct);
        var evaluation = await db.Evaluations.AsNoTracking()
            .Select(x => new { x.StartedAt, x.EndsAt })
            .SingleOrDefaultAsync(ct);
        return Results.Ok(new
        {
            mode = options.Value.IsSaas ? "saas" : "self_hosted",
            plan = plan.Code,
            limits = plan.Limits,
            evaluation = evaluation is null ? null : new EvaluationView(
                evaluation.StartedAt, evaluation.EndsAt, evaluation.EndsAt <= clock.GetUtcNow()),
            usage = new { humans = facts.Humans, agents = facts.Agents, projects = facts.Projects, storageBytes = facts.StorageBytes },
        });
    }

    private static async Task<IResult> GetSubscription(ICurrentTenant tenant, BillingDbContext db,
        IOrganizationPlanUsageSource usage, IOrganizationBillingState state, BillingAvailability availability,
        IOptions<BillingOptions> options, IOptions<StripeOptions> stripe, TimeProvider clock, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        var facts = await usage.GetAsync(organizationId, ct);
        if (facts is null) return Results.NotFound();
        // The plan the organization is held to, which during an evaluation is Hosted rather
        // than the Free the stored column still says.
        var effective = await BillingPlans.EffectiveCodeAsync(
            state, organizationId, facts.PlanCode, !options.Value.IsSaas, ct);
        if (!options.Value.IsSaas)
        {
            // Self-hosted: nothing is billed and nothing is read, so the answer is fixed.
            return Results.Ok(new SubscriptionView("self_hosted", false, effective, null, SubscriptionStatus.None,
                null, false, new SeatsView(0, 0), null, null, false, false, null, null, []));
        }

        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(ct);
        var plans = await db.Plans.AsNoTracking().Where(p => p.Code != PlanCodes.SelfHosted).ToListAsync(ct);
        var planOptions = plans
            .OrderBy(p => p.OrganizationPrice ?? p.HumanSeatPrice).ThenBy(p => p.Code)
            .Select(p => new PlanOptionView(p.Code, p.HumanSeatPrice, p.OrganizationPrice, p.IncludedAgentsPerHuman,
                p.Limits,
                // Purchasable means checkout can build a line for it: Free is always there
                // as the downgrade target, and the only priced plan is the
                // flat hosted one. The legacy seat plans stay listed for the subscriptions
                // that still carry them; nothing sells them.
                availability.IsEnabled && (p.Code == PlanCodes.Free
                    || BillingUsage.IsFlat(p) && stripe.Value.OrganizationPrice(p.Code) is not null)))
            .ToList();

        var now = clock.GetUtcNow();
        var evaluationRow = await db.Evaluations.AsNoTracking()
            .Select(x => new { x.StartedAt, x.EndsAt })
            .SingleOrDefaultAsync(ct);
        var evaluation = evaluationRow is null
            ? null
            : new EvaluationView(evaluationRow.StartedAt, evaluationRow.EndsAt, evaluationRow.EndsAt <= now);
        var hostedPrice = plans.FirstOrDefault(p => p.Code == PlanCodes.Hosted)?.OrganizationPrice ?? 0;
        FoundingOfferView? founding = null;
        if (subscription?.FoundingPrice is { } foundingPrice)
        {
            var periods = subscription.FoundingPeriods ?? options.Value.FoundingPeriods;
            var remaining = Math.Max(0, periods - subscription.FoundingPeriodsBilled);
            var endsAt = subscription.FoundingConvertedAt is null && subscription.CurrentPeriodEnd is { } periodEnd
                ? (DateTimeOffset?)periodEnd.AddMonths(remaining)
                : null;
            founding = new FoundingOfferView(foundingPrice, periods, subscription.FoundingPeriodsBilled,
                hostedPrice, subscription.FoundingConvertedAt, endsAt);
        }

        return Results.Ok(new SubscriptionView(
            "saas",
            availability.IsEnabled,
            effective,
            subscription?.Status.IsBilling() == true ? subscription.Plan : null,
            subscription?.Status ?? SubscriptionStatus.None,
            subscription?.CurrentPeriodEnd,
            subscription?.CancelAtPeriodEnd ?? false,
            new SeatsView(subscription?.SeatsHuman ?? 0, subscription?.SeatsAgent ?? 0),
            subscription?.PaymentFailedAt,
            subscription?.GraceEndsAt,
            // Asked rather than recomputed: BillingOrganizationState is what every write
            // path consults, and a banner that disagrees with the refusal is worse than no
            // banner. In particular a paid subscription answers for an organization whose
            // evaluation expired long ago.
            await state.IsReadOnlyAsync(organizationId, ct),
            subscription?.StripeCustomerId is not null,
            evaluation,
            founding,
            planOptions));
    }

    /// <summary>
    /// Moves the organization to a plan. With no subscription that is a Stripe Checkout
    /// page; with one it is an in-place, prorated change (or, for Free, a cancellation at
    /// the end of the paid period), confirmed by the webhook that follows.
    ///
    /// Checkout sells one plan: the flat hosted subscription. The legacy
    /// seat plans are closed to new subscriptions - the only legacy move still possible is
    /// refreshing the very plan an existing subscription already carries - and Free remains
    /// the downgrade target. A downgrade that would leave the organization over its limits
    /// is refused with the list of what to remove first. When the founding offer is running
    /// and this organization has never had it, checkout charges the founding price; the
    /// grant is made here, from configuration, never from the request.
    /// </summary>
    private static async Task<IResult> Checkout(CheckoutRequest request, string orgSlug, HttpContext http,
        ICurrentTenant tenant, ICurrentUser user, BillingDbContext db, BillingUsage usage,
        BillingAvailability availability, IStripeGateway stripe, IOptions<StripeOptions> stripeOptions,
        IOptions<BillingOptions> billingOptions, IOptions<EmailOptions> email, IUserDirectory users, CancellationToken ct)
    {
        if (!availability.IsEnabled) return Unavailable(availability.UnavailableReason);
        var organizationId = tenant.OrganizationId!.Value;

        var code = request.Plan?.Trim().ToLowerInvariant() ?? "";
        var target = code == PlanCodes.SelfHosted ? null
            : await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Code == code, ct);
        if (target is null) return Invalid("plan", "Choose a plan this instance sells.");

        var flat = BillingUsage.IsFlat(target);
        var legacyRefresh = !flat && target.Code != PlanCodes.Free;
        if (legacyRefresh)
        {
            var current = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(ct);
            legacyRefresh = current is { StripeSubscriptionId: not null } row
                && row.Status.IsBilling() && row.Plan == target.Code;
        }
        if (legacyRefresh)
        {
            // An existing customer's plan keeps working exactly as it did - but only for
            // that customer. Nothing here silently changes anyone's price.
            legacyRefresh = stripeOptions.Value.HumanPrice(target.Code) is not null;
        }
        else if (!flat && target.Code != PlanCodes.Free)
        {
            return Invalid("plan", $"{target.Code} is no longer open to new subscriptions. Choose {PlanCodes.Hosted}.");
        }

        var priced = target.Code == PlanCodes.Free
            || (flat
                ? stripeOptions.Value.OrganizationPrice(target.Code) is not null
                : stripeOptions.Value.HumanPrice(target.Code) is not null);
        if (!priced) return Invalid("plan", "Choose a plan this instance sells.");

        var facts = await usage.GetAsync(organizationId, includeStorage: true, ct);
        if (facts is null) return Results.NotFound();

        var exceeded = BillingUsage.Exceeded(facts, target.Limits);
        if (exceeded.Count > 0)
        {
            return Results.Problem(
                title: "The organization uses more than that plan allows.",
                detail: $"Before moving to {target.Code}, remove: {string.Join("; ", exceeded.Select(Describe))}.",
                type: ProblemTypes.PlanDowngradeBlocked,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["plan"] = target.Code, ["exceeded"] = exceeded });
        }

        var subscription = await db.Subscriptions.SingleOrDefaultAsync(ct);
        var founding = flat && target.Code == PlanCodes.Hosted
            && billingOptions.Value.FoundingPrice is { }
            && subscription?.FoundingPrice is null;

        if (subscription is { StripeSubscriptionId: { } subscriptionId } && subscription.Status.IsBilling())
        {
            if (target.Code == subscription.Plan && !subscription.CancelAtPeriodEnd)
            {
                return Invalid("plan", $"The organization is already on {target.Code}.");
            }
            if (target.Code == PlanCodes.Free)
            {
                await stripe.CancelAtPeriodEndAsync(subscriptionId, ct);
                return Results.Ok(new BillingRedirectView(null, true));
            }
            var seats = BillingUsage.Seats(facts.Humans, facts.Agents, target);
            var lines = founding ? BillingUsage.FoundingLines(stripeOptions.Value, target.Code)
                : flat ? BillingUsage.FlatLines(stripeOptions.Value, target.Code)
                : BillingUsage.Lines(stripeOptions.Value, target.Code, seats);
            if (lines is null) return Unavailable($"Prices for {target.Code} are not fully configured on this instance.");
            if (founding) GrantFounding(subscription, billingOptions.Value);
            await stripe.UpdateSubscriptionAsync(subscriptionId, target.Code, lines, ct,
                // The founding price takes over cleanly: the discounted periods are whole
                // months, and a proration credit would only confuse what they cost.
                proration: founding ? "none" : "create_prorations");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new BillingRedirectView(null, true));
        }

        if (target.Code == PlanCodes.Free)
        {
            return Invalid("plan", "The organization is already on the free plan.");
        }

        var checkoutLines = founding ? BillingUsage.FoundingLines(stripeOptions.Value, target.Code)
            : flat ? BillingUsage.FlatLines(stripeOptions.Value, target.Code)
            : BillingUsage.Lines(stripeOptions.Value, target.Code, BillingUsage.Seats(facts.Humans, facts.Agents, target));
        if (checkoutLines is null) return Unavailable($"Prices for {target.Code} are not fully configured on this instance.");

        var profiles = await users.GetDeliveryProfilesAsync([user.UserId!], ct);
        var returnTo = SettingsUrl(http, email.Value, orgSlug);
        var url = await stripe.CreateCheckoutSessionAsync(new StripeCheckoutRequest(
            organizationId,
            target.Code,
            subscription?.StripeCustomerId,
            profiles.TryGetValue(user.UserId!, out var profile) ? profile.Email : null,
            checkoutLines,
            $"{returnTo}?checkout=success",
            $"{returnTo}?checkout=canceled",
            Founding: founding), ct);
        return Results.Ok(new BillingRedirectView(url, false));
    }

    /// <summary>
    /// Stamps the founding offer on the subscription row, tracked but not yet saved: the
    /// gateway call that follows confirms it, and a gateway failure leaves the row
    /// untouched because nothing was saved. The Stripe request carries the flag too, so a
    /// fresh subscription is stamped by the checkout webhook instead.
    /// </summary>
    private static void GrantFounding(Subscription subscription, BillingOptions options)
    {
        subscription.FoundingPrice = options.FoundingPrice;
        subscription.FoundingPeriods = options.FoundingPeriods;
        subscription.FoundingPeriodsBilled = 0;
        subscription.FoundingConvertedAt = null;
    }

    private static async Task<IResult> Portal(string orgSlug, HttpContext http, BillingDbContext db,
        BillingAvailability availability, IStripeGateway stripe, IOptions<EmailOptions> email, CancellationToken ct)
    {
        if (!availability.IsEnabled) return Unavailable(availability.UnavailableReason);
        var customerId = await db.Subscriptions.AsNoTracking().Select(x => x.StripeCustomerId).SingleOrDefaultAsync(ct);
        if (customerId is null)
        {
            return Results.Problem(
                title: "No billing account yet.",
                detail: "Invoices and payment methods appear once the organization has chosen a paid plan.",
                type: ProblemTypes.Conflict,
                statusCode: StatusCodes.Status409Conflict);
        }
        var url = await stripe.CreatePortalSessionAsync(customerId, SettingsUrl(http, email.Value, orgSlug), ct);
        return Results.Ok(new BillingRedirectView(url, false));
    }

    private static async Task<IResult> ReceiveWebhook(HttpRequest request, BillingAvailability availability,
        StripeWebhookProcessor processor, CancellationToken ct)
    {
        // Like GitHub's: an instance that does not bill has nothing at this address.
        if (!availability.IsEnabled) return Results.NotFound();

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxWebhookBytes;
        }
        if (request.ContentLength > MaxWebhookBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // The signature is over the exact bytes Stripe sent, so the body is read raw and
        // never through a model binder that might normalise it.
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(ct);
        var outcome = await processor.ProcessAsync(payload, request.Headers["Stripe-Signature"].ToString(), ct);
        return outcome == StripeWebhookOutcome.Rejected
            ? Results.Problem(title: "Invalid Stripe signature.", statusCode: StatusCodes.Status400BadRequest)
            : Results.Ok(new { received = true, duplicate = outcome == StripeWebhookOutcome.Duplicate });
    }

    /// <summary>
    /// Where Stripe sends the browser back. <c>Email:BaseUrl</c> wins when set, as for every
    /// other link Aictiq hands out; otherwise the request's own origin, which is right here -
    /// the person who clicked is the person coming back, in the same browser.
    /// </summary>
    private static string SettingsUrl(HttpContext http, EmailOptions email, string orgSlug)
    {
        var origin = string.IsNullOrWhiteSpace(email.BaseUrl)
            ? $"{http.Request.Scheme}://{http.Request.Host}"
            : email.BaseUrl.TrimEnd('/');
        return $"{origin}/o/{Uri.EscapeDataString(orgSlug)}/settings/billing";
    }

    private static string Describe(ExceededLimit limit) => limit.Limit switch
    {
        "seats_human" => $"{limit.Used - limit.Allowed} human member(s) ({limit.Used} of {limit.Allowed} allowed)",
        "seats_agent" => $"{limit.Used - limit.Allowed} agent(s) ({limit.Used} of {limit.Allowed} allowed)",
        "projects" => $"{limit.Used - limit.Allowed} active project(s) ({limit.Used} of {limit.Allowed} allowed)",
        "storage_bytes" => $"{(limit.Used - limit.Allowed) / 1_048_576.0:N0} MB of attachments",
        _ => limit.Limit,
    };

    private static IResult Unavailable(string detail) =>
        Results.Problem(title: "Billing is not available.", detail: detail,
            type: ProblemTypes.BillingUnavailable, statusCode: StatusCodes.Status409Conflict);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, type: ProblemTypes.Validation);
}
