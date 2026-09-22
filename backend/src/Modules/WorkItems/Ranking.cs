using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;

namespace Aictiq.Modules.WorkItems;

/// <summary>Compact base-36 ranks.  A rank is ordered ordinally, so it remains portable in URLs and SQL.</summary>
internal static class ItemRanking
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int Width = 16;
    private static readonly BigInteger Max = BigInteger.Pow(36, Width) - 1;

    public static string Between(string? after, string? before)
    {
        var left = after is null ? BigInteger.Zero : Parse(after);
        var right = before is null ? Max : Parse(before);
        if (right - left > 1) return Format((left + right) / 2);
        // No fixed-width space remains. The lexical extension still falls between the
        // neighbours; the caller schedules a rebalance before this becomes unwieldy.
        return after is null ? "0h" : after + "h";
    }

    public static async Task<string> AfterLastAsync(WorkItemsDbContext db, Guid projectId, CancellationToken ct) =>
        Between(await db.Items.Where(x => x.ProjectId == projectId && x.Rank != null).OrderByDescending(x => x.Rank).Select(x => x.Rank).FirstOrDefaultAsync(ct), null);

    public static async Task RebalanceAsync(WorkItemsDbContext db, Guid projectId, CancellationToken ct)
    {
        var items = await db.Items.Where(x => x.ProjectId == projectId).OrderBy(x => x.Rank).ThenBy(x => x.Number).ToListAsync(ct);
        var spacing = Max / (items.Count + 1);
        for (var index = 0; index < items.Count; index++) items[index].Rank = Format(spacing * (index + 1));
    }

    private static BigInteger Parse(string rank)
    {
        BigInteger value = 0;
        foreach (var c in rank.Take(Width)) { var digit = Alphabet.IndexOf(c); if (digit < 0) return BigInteger.Zero; value = value * 36 + digit; }
        return value * BigInteger.Pow(36, Math.Max(0, Width - Math.Min(rank.Length, Width)));
    }
    private static string Format(BigInteger value)
    {
        var chars = new char[Width];
        for (var index = Width - 1; index >= 0; index--) { chars[index] = Alphabet[(int)(value % 36)]; value /= 36; }
        return new string(chars);
    }
}
