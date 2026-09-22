using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;

namespace Aictiq.Modules.Notifications.Templates;

/// <summary>A template rendered into the three parts a message needs.</summary>
public sealed record RenderedEmail(string Subject, string Html, string Text);

public sealed class EmailTemplateNotFoundException(string template)
    : InvalidOperationException($"No email template named '{template}'.");

/// <summary>
/// Renders the Liquid templates embedded in this assembly. A template is three files -
/// <c>{name}.subject.liquid</c>, <c>{name}.html.liquid</c>, <c>{name}.text.liquid</c> -
/// wrapped by <c>_layout.html.liquid</c> and <c>_layout.text.liquid</c>.
///
/// Liquid rather than string interpolation because these are the strings most likely to
/// be edited by someone who is not editing C# that week, and because a template that
/// cannot reach into the object graph cannot leak what it was not given.
/// </summary>
/// <remarks>
/// The HTML parts render through <see cref="HtmlEncoder"/>: every variable is someone's
/// name, an item title or an organization name - user- and agent-written text arriving in
/// a client that will happily render what it is given. The one value not escaped is the
/// layout's <c>content</c>, which is the already rendered, already escaped body.
/// </remarks>
public sealed class EmailTemplateRenderer
{
    /// <summary>Available to callers assembling variables, and the layout's fallback heading.</summary>
    public const string ProductName = "Aictiq";

    private const string ResourcePrefix = "Aictiq.Modules.Notifications.Templates.";

    private static readonly FluidParser Parser = new();
    private static readonly Assembly Assembly = typeof(EmailTemplateRenderer).Assembly;

    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new();

    public RenderedEmail Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (!Exists(template))
        {
            throw new EmailTemplateNotFoundException(template);
        }

        // A subject is a header, not markup: escaping it would put &amp; in someone's inbox.
        var subject = Render($"{template}.subject", Context(variables), NullEncoder.Default).Trim();
        var html = Render($"{template}.html", Context(variables), HtmlEncoder.Default);
        var text = Render($"{template}.text", Context(variables), NullEncoder.Default);

        return new RenderedEmail(
            subject,
            Wrap("_layout.html", variables, subject, html, HtmlEncoder.Default),
            Wrap("_layout.text", variables, subject, text, NullEncoder.Default).Trim() + "\n");
    }

    /// <summary>Whether a template of this name ships in the assembly.</summary>
    public bool Exists(string template) =>
        Assembly.GetManifestResourceInfo($"{ResourcePrefix}{template}.subject.liquid") is not null;

    private string Wrap(
        string layout, IReadOnlyDictionary<string, string> variables,
        string subject, string body, TextEncoder encoder)
    {
        var context = Context(variables);
        context.SetValue("subject", subject);
        // Already rendered - and, for the HTML layout, already escaped. Encode it a second
        // time and the recipient reads the markup instead of the message.
        context.SetValue("content", new StringValue(body, encode: false));

        return Render(layout, context, encoder);
    }

    private string Render(string name, TemplateContext context, TextEncoder encoder) =>
        Get(name).Render(context, encoder);

    private static TemplateContext Context(IReadOnlyDictionary<string, string> variables)
    {
        var context = new TemplateContext();
        context.SetValue("productName", ProductName);
        foreach (var (key, value) in variables)
        {
            context.SetValue(key, value);
        }

        return context;
    }

    private IFluidTemplate Get(string name) => _cache.GetOrAdd(name, static key =>
    {
        using var stream = Assembly.GetManifestResourceStream($"{ResourcePrefix}{key}.liquid")
            ?? throw new EmailTemplateNotFoundException(key);
        using var reader = new StreamReader(stream);

        return Parser.TryParse(reader.ReadToEnd(), out var template, out var error)
            ? template
            // A template that does not parse is a build-time mistake that only surfaces
            // here, so say which one and why rather than failing the send opaquely.
            : throw new InvalidOperationException($"Email template '{key}' does not parse: {error}");
    });
}
