var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);
var app = builder.Build();
app.UseWebSockets();
app.MapVoxaVoice("/voice").UseDefaults();
app.Run();
