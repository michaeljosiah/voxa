var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);   // same providers/profile as any Voxa host
var app = builder.Build();

app.UseWebSockets();
app.MapVoxaTwilioVoice("/twilio");                 // webhook (TwiML) + /twilio/media (WebSocket)
app.Run();
