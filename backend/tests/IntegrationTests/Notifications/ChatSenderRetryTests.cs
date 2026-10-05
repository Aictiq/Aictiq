using System.Net;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aictiq.IntegrationTests.Notifications;

/// <summary>
/// The chat outbox owns retries. A chat client that also retried on its own would post a
/// message twice when the platform took it but the answer was lost, and would hit a dead
/// webhook several times per outbox attempt.
/// </summary>
[Trait("Category", "Notifications")]
public sealed class ChatSenderRetryTests
{
    [Fact]
    public async Task A_failed_send_posts_once_even_though_service_defaults_add_retries()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.Services.AddNotificationsModule();
        var platform = new FailingPlatform();
        builder.Services.AddHttpClient(ChatSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => platform);
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var slack = scope.ServiceProvider.GetServices<IChatSender>().Single(sender => sender.Type == ChatChannelType.Slack);

        var failure = await Assert.ThrowsAsync<ChatDeliveryException>(() =>
            slack.SendAsync("https://hooks.slack.com/services/T/B/x", "hello", TestContext.Current.CancellationToken));

        Assert.Equal("Slack answered 500.", failure.Message);
        Assert.Equal(1, platform.Requests);
    }

    private sealed class FailingPlatform : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
