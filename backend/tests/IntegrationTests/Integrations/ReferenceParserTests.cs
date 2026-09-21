using Aictiq.Modules.Integrations;

namespace Aictiq.IntegrationTests.Integrations;

[Trait("Category", "Integrations")]
public sealed class ReferenceParserTests
{
    [Theory]
    [InlineData("Ship ACME-12 and ABC9-7", true, new[] { "ACME-12", "ABC9-7" })]
    [InlineData("Fix #12", false, new string[0])]
    [InlineData("Fix #12", true, new[] { "#12" })]
    [InlineData("`ACME-12`\n```\nABC-3\n```\nhttps://example.test/ACME-8 ACME-12", true, new[] { "ACME-12" })]
    [InlineData("ACME-12 ACME-12 #12 #12", true, new[] { "ACME-12", "#12" })]
    public void parses_only_actionable_references(string text, bool allowLocalReferences, string[] expected)
    {
        var actual = ReferenceParser.Parse(text, allowLocalReferences)
            .Select(reference => reference.ProjectKey is null ? $"#{reference.ItemNumber}" : $"{reference.ProjectKey}-{reference.ItemNumber}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("acme-12-login", "ACME-12")]
    [InlineData("feature/AbC9-7-more", "ABC9-7")]
    public void parses_case_insensitive_branch_references(string branch, string expected)
    {
        var reference = Assert.Single(ReferenceParser.ParseBranch(branch));
        Assert.Equal(expected, $"{reference.ProjectKey}-{reference.ItemNumber}");
    }
}
