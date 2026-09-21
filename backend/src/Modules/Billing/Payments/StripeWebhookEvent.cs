using System.Text.Json;

namespace Aictiq.Modules.Billing.Payments;

/// <summary>
/// The few fields of a Stripe event Aictiq acts on, read straight from the JSON.
///
/// Deliberately not Stripe.net's typed event model: those types follow the SDK's pinned API
/// version, and Stripe sends each webhook endpoint the version it was created with. A field
/// that moved between versions (a subscription's period moved onto its items in 2025-03;
/// an invoice's subscription moved under <c>parent</c>) would silently read as null. Reading
/// both places by hand is a dozen lines and does not break on the next SDK upgrade.
/// </summary>
public sealed record StripeWebhookEvent(string Id, string Type, DateTimeOffset Created, JsonElement Object)
{
    public static StripeWebhookEvent? TryParse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || String(root, "id") is not { } id
                || String(root, "type") is not { } type
                || !root.TryGetProperty("created", out var created) || Number(created) is not { } seconds
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return new StripeWebhookEvent(id, type, DateTimeOffset.FromUnixTimeSeconds(seconds), obj.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? Get(string property) => String(Object, property);

    public string? Metadata(string key) =>
        Object.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? String(metadata, key)
            : null;

    /// <summary>An expandable reference: Stripe sends either the id or, when expanded, the object.</summary>
    public string? Reference(string property) =>
        Object.TryGetProperty(property, out var value) ? ReferenceOf(value) : null;

    public bool Bool(string property) =>
        Object.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The subscription an invoice is for, in either the old or the 2025-03+ shape.</summary>
    public string? InvoiceSubscription() =>
        Reference("subscription")
        ?? (Object.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty("subscription_details", out var details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("subscription", out var subscription)
                ? ReferenceOf(subscription)
                : null);

    /// <summary>A subscription's items as (price id, quantity, period start, period end).</summary>
    public IReadOnlyList<(string PriceId, long Quantity, DateTimeOffset? PeriodStart, DateTimeOffset? PeriodEnd)> SubscriptionItems()
    {
        var items = new List<(string, long, DateTimeOffset?, DateTimeOffset?)>();
        if (!Object.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Object
            || !list.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return items;
        }
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var price = item.TryGetProperty("price", out var priceNode) ? ReferenceOf(priceNode)
                : item.TryGetProperty("plan", out var planNode) ? ReferenceOf(planNode)
                : null;
            if (price is null) continue;
            var quantity = item.TryGetProperty("quantity", out var q) ? Number(q) ?? 0 : 0;
            items.Add((price, quantity, Time(item, "current_period_start"), Time(item, "current_period_end")));
        }
        return items;
    }

    /// <summary>The billing period: on the subscription before API 2025-03, on its items after.</summary>
    public (DateTimeOffset? Start, DateTimeOffset? End) Period()
    {
        var start = Time(Object, "current_period_start");
        var end = Time(Object, "current_period_end");
        if (start is not null && end is not null) return (start, end);
        var items = SubscriptionItems();
        return (items.Select(x => x.PeriodStart).Where(x => x is not null).Min(),
            items.Select(x => x.PeriodEnd).Where(x => x is not null).Max());
    }

    private static string? ReferenceOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Object => String(value, "id"),
        _ => null,
    };

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && Number(value) is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static long? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
