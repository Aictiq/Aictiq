using ModelContextProtocol;

namespace Aictiq.SharedKernel.Mcp;

/// <summary>
/// Thrown by an MCP tool that has no value to return: the thing it was asked about does
/// not exist or cannot be seen ("item ACME-9 not found or no access" - the same words the
/// REST surface answers a 404 with), or it lost a compare-and-swap ("conflict: version
/// changed"). The message is the tool's answer: the shared CallTool filter turns it into
/// a text content block on a successful result, because an agent on the far side of the
/// CLI cannot act on - or even distinguish - a content-less result. Inheriting
/// McpException keeps the text intact even if a future filter forgets to convert it.
/// </summary>
public sealed class McpAnswerException(string message) : McpException(message);
