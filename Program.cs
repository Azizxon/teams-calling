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

builder.Services.AddSingleton<ICallSessionStore, InMemoryCallSessionStore>();
builder.Services.AddSingleton<ICallNotificationArchiver, FileCallNotificationArchiver>();

// BotService owns ICommunicationsClient and all call/media lifecycle.
// Register as both singleton (for direct injection into CallsController) and
// as a hosted service so it starts/stops with the application.
builder.Services.AddSingleton<BotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotService>());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

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