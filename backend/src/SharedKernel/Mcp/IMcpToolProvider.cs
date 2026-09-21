using System.Reflection;

namespace Aictiq.SharedKernel.Mcp;

/// <summary>
/// Declares the assembly that contributes MCP tools for a module. The API host discovers
/// only these assemblies, so adding an attributed class elsewhere cannot accidentally
/// become part of the public agent surface.
/// </summary>
public interface IMcpToolProvider
{
    Assembly ToolAssembly { get; }
}

public sealed class McpToolProvider(Assembly toolAssembly) : IMcpToolProvider
{
    public Assembly ToolAssembly { get; } = toolAssembly;
}
