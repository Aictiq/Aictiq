using Aictiq.Modules.Identity;
using Aictiq.Modules.Integrations;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.Wiki;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Analytics;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Billing;
using Aictiq.Modules.Automation;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDataSource("appdb");

builder.Services.AddSharedKernel();
// Workers need the store to garbage-collect blobs whose metadata was deleted.
builder.Services.AddBlobStorage(builder.Configuration);
// Workers are where email actually leaves the building.
builder.Services.AddEmail(builder.Configuration);
builder.Services.AddHybridCache();
builder.Services.AddSingleton<ICurrentUser, SystemCurrentUser>();
// Outbox handlers (item changes, in-app notifications) push to browsers too. Workers have
// no hub, so they publish over NOTIFY and the API forwards to its connections.
builder.Services.AddRealtimeNotifyPublisher(builder.Configuration);

// Workers needs the whole Identity module, not just its sweeps: the agent-ownership
// handler reaches accounts through IAgentIdentities, which Identity implements.
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddIdentityWorkers(builder.Configuration);
builder.Services.AddTenancyModule();
builder.Services.AddBillingModule(builder.Configuration);
builder.Services.AddBillingWorkers();
builder.Services.AddNotificationsModule();
builder.Services.AddNotificationsWorkers(builder.Configuration);
builder.Services.AddWorkItemsModule();
builder.Services.AddWorkItemsWorkers();
builder.Services.AddWikiModule();
builder.Services.AddWikiWorkers();
builder.Services.AddIntegrationsModule(builder.Configuration);
builder.Services.AddIntegrationsWorkers();
builder.Services.AddAnalyticsModule(builder.Configuration);
builder.Services.AddAnalyticsWorkers(builder.Configuration);
builder.Services.AddAutomationModule(builder.Configuration);
builder.Services.AddAutomationWorkers();

// Single consumer of shared.outbox_messages (integration events raised by any module).
// Every assembly that can raise an integration event must be listed as modules land, or
// its messages cannot be deserialized and are dead-lettered instead of delivered.
builder.Services.AddOutboxProcessor(
    // SendEmailRequested lives in SharedKernel: any module may ask for an email, so the
    // event cannot belong to the module that sends it.
    typeof(Aictiq.SharedKernel.Email.SendEmailRequested).Assembly,
    typeof(Aictiq.Modules.Tenancy.TenancyDbContext).Assembly,
    typeof(Aictiq.Modules.WorkItems.WorkItemsDbContext).Assembly,
    typeof(Aictiq.Modules.Wiki.WikiDbContext).Assembly,
    typeof(Aictiq.Modules.Integrations.IntegrationsDbContext).Assembly);

// Workers run as a separate process. They expose only health endpoints over HTTP; all
// real work happens in BackgroundServices registered by the modules' AddXWorkers.
// The API owns migrations - never call Database.Migrate from here.

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();

public partial class Program { }
