using System.Reflection;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Outbox;

/// <summary>
/// Maps the type name stored in shared.outbox_messages back to a CLR type. Built from an
/// explicit assembly list so payloads can't be deserialized into arbitrary types.
/// </summary>
public sealed class IntegrationEventTypeRegistry
{
    private readonly Dictionary<string, Type> _types;

    public IntegrationEventTypeRegistry(IEnumerable<Assembly> assemblies)
    {
        _types = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IIntegrationEvent).IsAssignableFrom(t))
            .ToDictionary(t => t.FullName!, t => t);
    }

    public Type? Resolve(string typeName) => _types.GetValueOrDefault(typeName);
}
