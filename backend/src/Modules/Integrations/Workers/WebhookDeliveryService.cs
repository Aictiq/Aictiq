using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.Modules.Integrations.Domain;

namespace Aictiq.Modules.Integrations.Workers;

/// <summary>Delivers the durable queue. A failed HTTP call never blocks the shared outbox.</summary>
public sealed class WebhookDeliveryService(
    IServiceScopeFactory scopes, IHttpClientFactory clients, WebhookSecretProtector secrets, IOptions<WebhooksOptions> options,
    TimeProvider clock, ILogger<WebhookDeliveryService> logger) : BackgroundService
{
    private readonly WebhooksOptions _options = options.Value;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Webhook delivery sweep failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Public for deterministic integration tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var now = clock.GetUtcNow();
        var rows = await db.WebhookDeliveries.IgnoreQueryFilters().Where(x => x.Status == "pending" && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).Take(_options.BatchSize).ToListAsync(cancellationToken);
        foreach (var row in rows) await DeliverAsync(db, row, cancellationToken);
        return rows.Count;
    }

    private async Task DeliverAsync(IntegrationsDbContext db, WebhookDelivery delivery, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == delivery.SubscriptionId, ct);
        if (subscription is null || !subscription.Active) { delivery.Status = "cancelled"; await db.SaveChangesAsync(ct); return; }
        delivery.Attempt++;
        try
        {
            var guardError = await WebhookUrlGuard.ValidateAsync(subscription.Url, _options.AllowPrivateNetworks, ct);
            if (guardError is not null) throw new InvalidOperationException(guardError);
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
            {
                Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Aictiq-Signature", WebhookSignature.Create(delivery.Payload, secrets.Unprotect(subscription.SecretProtected)));
            request.Headers.TryAddWithoutValidation("X-Aictiq-Event", delivery.EventName);
            request.Headers.TryAddWithoutValidation("X-Aictiq-Delivery", delivery.Id.ToString());
            using var response = await clients.CreateClient(IntegrationsModule.WebhookHttpClientName).SendAsync(request, ct);
            delivery.StatusCode = (int)response.StatusCode;
            delivery.ResponseExcerpt = await ExcerptAsync(response, ct);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                delivery.Status = "delivered"; delivery.DeliveredAt = clock.GetUtcNow(); delivery.LastError = null;
                subscription.ConsecutiveFailures = 0; subscription.UpdatedAt = clock.GetUtcNow();
            }
            else await FailAsync(delivery, subscription, $"Receiver returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { await FailAsync(delivery, subscription, ex.Message); }
        await db.SaveChangesAsync(ct);

    }

    private Task FailAsync(WebhookDelivery delivery, WebhookSubscription subscription, string error)
    {
        delivery.LastError = error.Length > 1000 ? error[..1000] : error;
        subscription.ConsecutiveFailures++;
        subscription.UpdatedAt = clock.GetUtcNow();
        if (subscription.ConsecutiveFailures >= 50) { subscription.Active = false; delivery.Status = "failed"; return Task.CompletedTask; }
        if (delivery.Attempt >= _options.MaxAttempts) { delivery.Status = "failed"; return Task.CompletedTask; }
        delivery.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(_options.MaxRetrySeconds, _options.BaseRetrySeconds * Math.Pow(2, delivery.Attempt - 1)));
        return Task.CompletedTask;
    }

    private static async Task<string?> ExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length > 2000 ? body[..2000] : body;
    }
}
