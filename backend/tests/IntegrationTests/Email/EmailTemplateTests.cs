using Aictiq.Modules.Notifications.Templates;

namespace Aictiq.IntegrationTests.Email;

/// <summary>
/// The templates themselves. No database and no relay: what is being proved is that every
/// template that ships parses, renders both bodies, and escapes what a person typed.
/// </summary>
[Trait("Category", "Email")]
public sealed class EmailTemplateTests
{
    private readonly EmailTemplateRenderer _renderer = new();

    [Theory]
    [InlineData("invitation")]
    [InlineData("mention")]
    [InlineData("assignment")]
    [InlineData("digest")]
    [InlineData("password-reset")]
    [InlineData("email-change")]
    public void every_shipped_template_renders_a_subject_and_both_bodies(string template)
    {
        var rendered = _renderer.Render(template, new Dictionary<string, string>
        {
            ["organizationName"] = "Acme",
            ["recipientName"] = "Ada",
            ["inviterName"] = "Grace",
            ["actorName"] = "Grace",
            ["roleName"] = "Member",
            ["acceptUrl"] = "https://aictiq.test/invitations/abc",
            ["itemKey"] = "ACME-12",
            ["itemTitle"] = "Ship the thing",
            ["itemUrl"] = "https://aictiq.test/items/ACME-12",
            ["inboxUrl"] = "https://aictiq.test/inbox",
            ["summary"] = "Two items moved.",
            ["resetUrl"] = "https://aictiq.test/reset-password/abc",
            ["confirmUrl"] = "https://aictiq.test/confirm-email/abc",
            ["newEmail"] = "augusta@example.com",
            ["currentEmail"] = "ada@example.com",
            ["expiresOn"] = "3 September 2026 18:00 UTC"
        });

        Assert.NotEmpty(rendered.Subject);
        // A subject is a header. Escaping it would put &amp; and &#39; in someone's inbox.
        Assert.DoesNotContain("&amp;", rendered.Subject);
        Assert.Contains("<html", rendered.Html);
        Assert.NotEmpty(rendered.Text);
        Assert.DoesNotContain("<html", rendered.Text);
    }

    [Fact]
    public void the_layout_wraps_the_body_and_carries_the_organization()
    {
        var rendered = _renderer.Render("invitation", new Dictionary<string, string>
        {
            ["organizationName"] = "Acme",
            ["inviterName"] = "Grace",
            ["roleName"] = "Member",
            ["acceptUrl"] = "https://aictiq.test/invitations/abc"
        });

        Assert.Contains("Acme", rendered.Html);
        Assert.Contains("https://aictiq.test/invitations/abc", rendered.Html);
        // The body is rendered into the layout, not next to it.
        Assert.Contains("Aictiq", rendered.Html);
        Assert.Contains("Acme", rendered.Text);
        Assert.Contains("https://aictiq.test/invitations/abc", rendered.Text);
    }

    [Fact]
    public void the_unsubscribe_footer_appears_only_when_there_is_a_link()
    {
        var variables = new Dictionary<string, string>
        {
            ["organizationName"] = "Acme",
            ["actorName"] = "Grace",
            ["itemKey"] = "ACME-12",
            ["itemTitle"] = "Ship it",
            ["itemUrl"] = "https://aictiq.test/items/ACME-12"
        };

        Assert.DoesNotContain("Manage which emails", _renderer.Render("mention", variables).Html);

        variables["unsubscribeUrl"] = "https://aictiq.test/settings/notifications";
        var withFooter = _renderer.Render("mention", variables);
        Assert.Contains("Manage which emails", withFooter.Html);
        Assert.Contains("https://aictiq.test/settings/notifications", withFooter.Text);
    }

    [Fact]
    public void html_bodies_escape_what_a_person_typed()
    {
        // Item titles, names and organization names are user- and agent-written text
        // arriving in a client that renders whatever it is given.
        var rendered = _renderer.Render("mention", new Dictionary<string, string>
        {
            ["organizationName"] = "Acme",
            ["actorName"] = "<script>alert(1)</script>",
            ["itemKey"] = "ACME-12",
            ["itemTitle"] = "Fix <b>everything</b>",
            ["itemUrl"] = "https://aictiq.test/items/ACME-12"
        });

        Assert.DoesNotContain("<script>", rendered.Html);
        Assert.DoesNotContain("Fix <b>", rendered.Html);
        Assert.Contains("&lt;script&gt;", rendered.Html);

        // The text part is not markup, so it is not escaped — and a client that renders
        // text/plain as HTML has a bug we cannot fix from here.
        Assert.Contains("<script>alert(1)</script>", rendered.Text);
    }

    [Fact]
    public void a_missing_variable_renders_empty_rather_than_throwing()
    {
        // Payloads travel through the outbox and are read back by a process that may be a
        // version ahead. A key that is not there yet must not dead-letter the message.
        var rendered = _renderer.Render("invitation", new Dictionary<string, string>
        {
            ["organizationName"] = "Acme",
            ["acceptUrl"] = "https://aictiq.test/invitations/abc"
        });

        Assert.NotEmpty(rendered.Html);
        Assert.DoesNotContain("{{", rendered.Html);
    }

    [Fact]
    public void an_unknown_template_says_so()
    {
        var error = Assert.Throws<EmailTemplateNotFoundException>(
            () => _renderer.Render("no-such-template", new Dictionary<string, string>()));

        Assert.Contains("no-such-template", error.Message);
        Assert.False(_renderer.Exists("no-such-template"));
        Assert.True(_renderer.Exists("invitation"));
    }
}
