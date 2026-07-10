// VDX-008 sample: the talker/thinker split. The interaction model (Voxa:Agent:*, a fast tier)
// keeps the conversation flowing; a heavyweight background "researcher" runs slow tools off the
// voice-latency critical path. Registering the background driver is the whole opt-in — the
// composer inserts the background stage, arms the hold/release arbitration, and gives the
// interaction model a delegate_task tool.
//
// Try it: run, open http://localhost:5000, and ask "look up the launch window for Artemis".
// The talker acknowledges immediately; ~8 s later the researcher's answer arrives as a new turn.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Voxa.Services.MicrosoftAgents;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoxa(builder.Configuration);

// The background researcher: its own agent, its own (heavier) model tier, its own backend tools.
// Slow is fine here — that's the point.
// Capture builder.Configuration (the repo's config-capture rule) rather than resolving
// IConfiguration from DI — plain ServiceCollection hosts don't register it.
var config = builder.Configuration;
builder.Services.AddVoxaBackgroundAgent(_ =>
{
    var apiKey = config["Voxa:OpenAI:ApiKey"]
        ?? throw new InvalidOperationException("Set Voxa:OpenAI:ApiKey (user-secrets or appsettings.Local.json).");
    var model = config["Voxa:BackgroundAgent:Model"] ?? "gpt-4o";

    var chatClient = new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient();
    var researcher = new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        Name = "researcher",
        ChatOptions = new ChatOptions
        {
            Instructions =
                "You are a background research assistant. Use your tools, then answer in 2-3 compact " +
                "sentences — your answer is read aloud by another assistant, so no lists or markdown.",
            Tools = [AIFunctionFactory.Create(SlowLookup)],
        },
    });
    return MicrosoftAgentVoice.CreateTurnDriver(researcher);
});

var app = builder.Build();
app.UseFileServer(); // the browser test page (same as MinimalServer)
app.UseWebSockets();
app.MapVoxaVoice("/voice").UseDefaults();
app.Run();

// A deliberately slow backend tool so the split is observable without any external API: the talker
// keeps the floor for the full 8 seconds while this runs.
[System.ComponentModel.Description("Look up detailed facts about a topic. Slow — takes several seconds.")]
static async Task<string> SlowLookup(string topic)
{
    await Task.Delay(TimeSpan.FromSeconds(8));
    return $"Reference notes on '{topic}': (demo data) the canonical source dates this to 2026, " +
           "notes two competing approaches, and recommends the simpler one for production use.";
}
