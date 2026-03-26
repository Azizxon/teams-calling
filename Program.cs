using teams_streaming_call.Configuration;
using teams_streaming_call.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddOptions<TeamsCallBotOptions>()
	.Bind(builder.Configuration.GetSection(TeamsCallBotOptions.SectionName))
	.ValidateDataAnnotations()
	.ValidateOnStart();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<ICallSessionStore, InMemoryCallSessionStore>();
builder.Services.AddSingleton<ICallNotificationArchiver, FileCallNotificationArchiver>();
builder.Services.AddSingleton<IMediaCaptureCoordinator, MediaCaptureCoordinator>();
builder.Services.AddSingleton<ICallNotificationProcessor, CallNotificationProcessor>();
builder.Services.AddHostedService<MediaPlatformInitializer>();

var app = builder.Build();

app.UseAuthorization();
app.MapControllers();

app.Run();