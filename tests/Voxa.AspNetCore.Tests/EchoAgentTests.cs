using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// VLS-001 WS4: the keyless Echo agent. Without it, "no API keys" dies at the LLM hop — STT and
/// TTS going local still left the default factory demanding an OpenAI key.
/// </summary>
public class EchoAgentTests
{
    private static (IVoiceAgentFactory Factory, ServiceProvider Sp) MetaPackageFactory(
        params (string Key, string? Value)[] config)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.ToDictionary(p => p.Key, p => p.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddVoxa(configuration);

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IVoiceAgentFactory>(), sp);
    }

    [Fact]
    public void Echo_Validates_With_Zero_Credentials()
    {
        var (factory, sp) = MetaPackageFactory();
        using var _ = sp;

        Assert.Empty(factory.Validate(new VoxaAgentOptions { Provider = "Echo" }));
        Assert.Empty(factory.Validate(new VoxaAgentOptions { Provider = "echo" })); // case-insensitive
    }

    [Fact]
    public void Missing_ApiKey_Error_Points_At_Echo_As_The_Keyless_Way_Out()
    {
        var (factory, sp) = MetaPackageFactory();
        using var _ = sp;

        var errors = factory.Validate(new VoxaAgentOptions { Provider = "OpenAI" });
        var error = Assert.Single(errors);
        Assert.Contains("Echo", error);
    }

    [Fact]
    public void Unknown_Provider_Error_Lists_Echo_As_A_Valid_Value()
    {
        var (factory, sp) = MetaPackageFactory();
        using var _ = sp;

        var errors = factory.Validate(new VoxaAgentOptions { Provider = "Anthropic" });
        var error = Assert.Single(errors);
        Assert.Contains("Anthropic", error);
        Assert.Contains("OpenAI", error);
        Assert.Contains("Echo", error);
    }

    [Fact]
    public void OpenAI_Path_Still_Validates_With_A_Key()
    {
        var (factory, sp) = MetaPackageFactory(("Voxa:OpenAI:ApiKey", "sk-test"));
        using var _ = sp;

        Assert.Empty(factory.Validate(new VoxaAgentOptions { Provider = "OpenAI" }));
        Assert.Empty(factory.Validate(new VoxaAgentOptions())); // null provider = OpenAI default
    }

    [Fact]
    public async Task Echo_Agent_Streams_The_Transcript_Back_In_Multiple_Chunks()
    {
        var (factory, sp) = MetaPackageFactory();
        using var _ = sp;

        var agent = factory.Create(sp, new VoxaAgentOptions { Provider = "Echo" });

        var chunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, "tell me about the weather")],
            session: null,
            cancellationToken: CancellationToken.None))
        {
            var chatUpdate = update.AsChatResponseUpdate();
            if (chatUpdate?.Contents is null) continue;
            foreach (var content in chatUpdate.Contents)
            {
                if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                    chunks.Add(text.Text);
            }
        }

        // Streamed in several chunks (so sentence aggregation + incremental TTS are exercised)...
        Assert.True(chunks.Count >= 2, $"expected multiple chunks, got {chunks.Count}");
        // ...that reassemble into the full deterministic reply, terminally punctuated for TTS.
        Assert.Equal("You said: tell me about the weather.", string.Concat(chunks));
    }

    [Fact]
    public async Task Echo_Loop_Is_Fully_Keyless_End_To_End_With_Local_Providers()
    {
        // The flagship config shape: local STT + local TTS + Echo agent, zero secrets anywhere.
        // The armed guard validating this at startup is the "no API keys" promise, enforced.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voxa:Stt"] = "WhisperCpp",
                ["Voxa:Tts"] = "Piper",
                ["Voxa:Agent:Provider"] = "Echo",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddVoxa(configuration);

        using var sp = services.BuildServiceProvider();
        var guard = sp.GetRequiredService<VoxaDefaultsGuard>();
        guard.Arm();

        await guard.StartAsync(CancellationToken.None); // throws on any validation error
    }
}
