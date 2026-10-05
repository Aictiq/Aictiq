using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>A Data Protection key, as the key ring stores it.</summary>
public sealed class DataProtectionKeyRow
{
    public int Id { get; init; }
    public string? FriendlyName { get; init; }
    public required string Xml { get; init; }
}

/// <summary>
/// The Data Protection key ring, kept in <c>notify.data_protection_keys</c>. The API encrypts
/// chat targets and webhook secrets that Workers decrypts, and each runs in its own
/// container: a key ring on either one's disk would be unreadable to the other and lost
/// with the container. In the database, both share one ring that survives redeploys.
/// </summary>
public sealed class PostgresDataProtectionKeys(NpgsqlDataSource dataSource) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var command = dataSource.CreateCommand("SELECT xml FROM notify.data_protection_keys ORDER BY id");
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read()) elements.Add(XElement.Parse(reader.GetString(0)));
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var command = dataSource.CreateCommand("INSERT INTO notify.data_protection_keys (friendly_name, xml) VALUES (@name, @xml)");
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
