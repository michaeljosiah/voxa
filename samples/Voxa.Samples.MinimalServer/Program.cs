var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);
var app = builder.Build();
app.UseFileServer(); // optional: serve the wwwroot browser test UI (not part of the voice-bot wiring)
app.UseWebSockets();
app.MapVoxaVoice("/voice").UseDefaults();
app.Run();
