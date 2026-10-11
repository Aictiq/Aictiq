using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Automation.Workers;

/// <summary>
/// Fleet mode's scale-from-zero, one organization per call from <see cref="RunSweeper"/>: when a
/// due run has waited past the organization's threshold, the customer's webhook is called so
/// their own Docker, KVM or Kubernetes can start an ephemeral runner. Aictiq starts nothing
/// itself. The call is claimed (<see cref="FleetSettings.LastScaleCallAt"/>, under the row
/// version) before it is made, so two workers never both call, and the cooldown counts from
/// the attempt whatever it answered - a failing endpoint is not hammered every sweep.
///
/// It also removes the ephemeral runners that stopped answering without removing themselves.
/// </summary>
public sealed class FleetScaler(
    AutomationDbContext db, ICurrentTenant tenant, FleetMode fleetMode, FleetSecrets secrets,
    IOrganizationLookup organizations, IHttpClientFactory httpClients, IOptions<AutomationOptions> options,
    IOptions<EmailOptions> email, TimeProvider clock, ILogger<FleetScaler> logger)
{
    public const string HttpClientName = "aictiq-fleet-webhook";

    public async Task SweepAsync(CancellationToken ct)
    {
        // One query for every organization that never opted in: without a settings row there is
        // no webhook, and no enrollment token an ephemeral runner could have come from.
        var settings = await db.FleetSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            return;
        }

        await RemoveLostEphemeralRunnersAsync(ct);
        await CallScaleWebhookAsync(settings, ct);
    }

    private async Task RemoveLostEphemeralRunnersAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - TimeSpan.FromMinutes(options.Value.EphemeralRunnerLostAfterMinutes);
        var lost = await db.Runners
            .Where(r => r.Ephemeral && r.DeletedAt == null && (r.LastSeenAt ?? r.CreatedAt) < cutoff)
            .Take(50)
            .ToListAsync(ct);
        foreach (var runner in lost)
        {
            // The same soft, final removal an Admin's delete is; a run it still held is the lost-run sweep's.
            runner.DeletedAt = now;
            runner.DisabledAt ??= now;
            runner.UpdatedAt = now;
        }
        if (lost.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task CallScaleWebhookAsync(FleetSettings settings, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (settings is not { Enabled: true, ScaleWebhookUrl: { } url, ScaleWebhookSecretProtected: { } protectedSecret }
            || !settings.ScaleCallDue(now))
        {
            return;
        }

        // Runs any runner may take; one sent to a particular runner waits for that runner, and a
        // new machine would not help it.
        var waiting = await db.Runs.AsNoTracking()
            .Where(run => run.Status == RunStatus.Queued && run.RequestedRunnerId == null
                && (run.ScheduledFor == null || run.ScheduledFor <= now))
            .Select(run => new { run.Harness, run.RequiredTags, Turn = run.ScheduledFor ?? run.QueuedAt })
            .ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return;
        }
        var oldest = waiting.Min(run => run.Turn);
        if (now - oldest < TimeSpan.FromSeconds(settings.ScaleAfterSeconds))
        {
            return;
        }

        var organizationId = tenant.OrganizationId!.Value;
        if (!(await fleetMode.GetAsync(organizationId, ct)).Active)
        {
            return;
        }

        // Claimed first: a second worker sweeping this organization now fails the row version.
        settings.LastScaleCallAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return;
        }

        var organization = await organizations.FindByIdAsync(organizationId, ct);
        var body = JsonSerializer.Serialize(new
        {
            @event = "fleet.scale_up",
            organization = organization?.Slug,
            aictiqUrl = string.IsNullOrWhiteSpace(email.Value.BaseUrl) ? null : email.Value.BaseUrl.TrimEnd('/'),
            sentAt = now,
            queue = new { waiting = waiting.Count, oldestWaitingSince = oldest },
            // What the new runner should be able to do, most-wanted first.
            demand = waiting
                .GroupBy(run => (run.Harness, Tags: string.Join(',', run.RequiredTags)))
                .Select(group => new
                {
                    harness = group.Key.Harness,
                    requiredTags = group.First().RequiredTags,
                    runs = group.Count(),
                })
                .OrderByDescending(group => group.runs)
                .Take(20),
        }, JsonSerializerOptions.Web);

        string? error;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-Aictiq-Event", "fleet.scale_up");
            request.Headers.TryAddWithoutValidation("X-Aictiq-Delivery", Guid.CreateVersion7().ToString());
            request.Headers.TryAddWithoutValidation("X-Aictiq-Signature", Sign(body, secrets.Unprotect(protectedSecret)));
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            error = response.IsSuccessStatusCode ? null : $"The webhook answered HTTP {(int)response.StatusCode}.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            error = "The webhook did not answer within 10 seconds.";
        }
        catch (HttpRequestException ex)
        {
            error = $"The webhook could not be reached: {ex.Message}";
        }
        catch (CryptographicException)
        {
            error = "The webhook secret could not be read. Rotate it in the fleet settings.";
        }

        if (error is not null)
        {
            logger.LogWarning("Fleet scale webhook failed for organization {OrganizationId}: {Error}", organizationId, error);
        }
        settings.LastScaleCallError = error is { Length: > FleetSettings.MaxWebhookErrorLength }
            ? error[..FleetSettings.MaxWebhookErrorLength]
            : error;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // An Admin saved the settings meanwhile; their save stands, and the next call reports again.
        }
    }

    /// <summary>The same signature outbound webhooks carry: <c>sha256=</c> and the hex HMAC of the body.</summary>
    public static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
