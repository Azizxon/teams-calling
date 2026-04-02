using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common.Telemetry;
using teams_streaming_call.Configuration;
using teams_streaming_call.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<TeamsCallBotOptions>()
    .Bind(builder.Configuration.GetSection(TeamsCallBotOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Graph SDK telemetry logger — required by CommunicationsClientBuilder.
builder.Services.AddSingleton<IGraphLogger>(sp =>
    new GraphLogger(typeof(Program).Assembly.GetName().Name ?? "TeamsBot"));

// Shared client-credentials token provider (cached, thread-safe).
builder.Services.AddSingleton<BotAuthenticationProvider>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<TeamsCallBotOptions>>().Value;
    var graphLogger = sp.GetRequiredService<IGraphLogger>();
    return new BotAuthenticationProvider(opts.AadAppId, opts.AadAppSecret, opts.TenantId, graphLogger);
});

// BotService owns ICommunicationsClient and all call/media lifecycle.
// Register as both singleton (for direct injection into CallsController) and
// as a hosted service so it starts/stops with the application.
builder.Services.AddSingleton<BotCallService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotCallService>());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Bot Framework adapter and bot.
// Reuse existing TeamsCallBot credentials (AadAppId / AadAppSecret / TenantId).
builder.Services.AddSingleton<BotFrameworkAuthentication>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<TeamsCallBotOptions>>().Value;
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MicrosoftAppType"]     = "SingleTenant",
            ["MicrosoftAppId"]       = opts.AadAppId,
            ["MicrosoftAppPassword"] = opts.AadAppSecret,
            ["MicrosoftAppTenantId"] = opts.TenantId,
        })
        .Build();
    return new ConfigurationBotFrameworkAuthentication(config);
});
builder.Services.AddSingleton<IBotFrameworkHttpAdapter, CloudAdapter>();
builder.Services.AddTransient<IBot, TeamsActivityBot>();

// Allow the controller to re-read the body for archiving.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.BufferBody = true);

var app = builder.Build();

// Enable request body buffering so CallsController can read it twice.
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});

app.UseAuthorization();
app.MapControllers();

app.Run();