using System.Net;
using Aictiq.SharedKernel.Http;
using Aictiq.SharedKernel.Text;

namespace Aictiq.IntegrationTests.Guards;

public sealed class OutboundGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fd12::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void private_and_reserved_addresses_are_not_public(string address) =>
        Assert.False(PublicNetworkGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void routable_addresses_are_public(string address) =>
        Assert.True(PublicNetworkGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task the_guarded_handler_refuses_to_connect_to_a_private_address()
    {
        using var handler = PublicNetworkGuard.CreateHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://127.0.0.1:9/", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-cmd", "\"'-cmd\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("plain title", "\"plain title\"")]
    [InlineData(null, "\"\"")]
    public void csv_cells_that_would_execute_as_formulas_are_made_inert(string? value, string expected) =>
        Assert.Equal(expected, CsvCell.Escape(value));
}
