using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.AspNetCore.Tests;

public class VoicePipelineBuilderTests
{
    [Fact]
    public void UseProcessor_With_HttpContext_Factory_Registers_For_Per_Connection_Construction()
    {
        var builder = new VoicePipelineBuilder();
        var ctxSeen = (HttpContext?)null;

        builder.UseProcessor(ctx =>
        {
            ctxSeen = ctx;
            return new CapturingProcessor();
        });

        Assert.Single(builder.ProcessorFactories);

        var fakeCtx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var processor = builder.ProcessorFactories[0](fakeCtx);

        Assert.NotNull(processor);
        Assert.Same(fakeCtx, ctxSeen);
        Assert.IsType<CapturingProcessor>(processor);
    }

    [Fact]
    public void UseProcessor_Stateless_Factory_Overload_Wraps_To_Per_Connection_Form()
    {
        var builder = new VoicePipelineBuilder();
        var calls = 0;

        builder.UseProcessor(() => { calls++; return new CapturingProcessor(); });

        Assert.Single(builder.ProcessorFactories);
        var ctx = new DefaultHttpContext();
        _ = builder.ProcessorFactories[0](ctx);
        _ = builder.ProcessorFactories[0](ctx);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void UseProcessor_Null_Factory_Throws()
    {
        var builder = new VoicePipelineBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.UseProcessor((Func<HttpContext, FrameProcessor>)null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseProcessor((Func<FrameProcessor>)null!));
    }

    [Fact]
    public void Fluent_Chain_Returns_Same_Builder_Instance()
    {
        var builder = new VoicePipelineBuilder();

        var result = builder
            .UseProcessor(() => new CapturingProcessor())
            .RequireAuthorization("PolicyA")
            .RequireCors("CorsA")
            .UseCustomFrameSerializer(_ => null);

        Assert.Same(builder, result);
    }

    // ── Authorization / CORS ───────────────────────────────────────────────

    [Fact]
    public void RequireAuthorization_Accumulates_Policies_In_Order()
    {
        var builder = new VoicePipelineBuilder();
        builder.RequireAuthorization("A");
        builder.RequireAuthorization("B", "C");

        Assert.Equal(new[] { "A", "B", "C" }, builder.AuthPolicies);
    }

    [Fact]
    public void RequireCors_Accumulates_Policies_In_Order()
    {
        var builder = new VoicePipelineBuilder();
        builder.RequireCors("X");
        builder.RequireCors("Y");

        Assert.Equal(new[] { "X", "Y" }, builder.CorsPolicies);
    }

    // ── UseWebSocketHello ──────────────────────────────────────────────────

    [Fact]
    public async Task UseWebSocketHello_Stores_Parsed_Value_On_HttpContext_Items()
    {
        var builder = new VoicePipelineBuilder();
        builder.UseWebSocketHello<TestHello>(
            (ws, ct) => ValueTask.FromResult(new TestHello { Greeting = "hi" }));

        Assert.NotNull(builder.HelloReader);

        var ctx = new DefaultHttpContext();
        await builder.HelloReader!(ctx, null!, CancellationToken.None);

        Assert.True(ctx.Items.ContainsKey(VoiceHello.HelloMetadataKey));
        var stored = ctx.Items[VoiceHello.HelloMetadataKey] as TestHello;
        Assert.NotNull(stored);
        Assert.Equal("hi", stored!.Greeting);
    }

    [Fact]
    public void UseWebSocketHello_Null_Reader_Throws()
    {
        var builder = new VoicePipelineBuilder();
        Assert.Throws<ArgumentNullException>(() =>
            builder.UseWebSocketHello<TestHello>(null!));
    }

    // ── Custom serializer ─────────────────────────────────────────────────

    [Fact]
    public void UseCustomFrameSerializer_Captures_Delegate()
    {
        var builder = new VoicePipelineBuilder();
        Func<Frame, string?> serializer = _ => "{\"type\":\"x\"}";
        builder.UseCustomFrameSerializer(serializer);

        Assert.Same(serializer, builder.CustomSerializer);
    }

    // ── End-to-end: factories actually produce processors ──────────────────

    [Fact]
    public void Multiple_UseProcessor_Calls_Preserve_Order()
    {
        var builder = new VoicePipelineBuilder();
        builder.UseProcessor(() => new NamedProcessor("first"));
        builder.UseProcessor(() => new NamedProcessor("second"));
        builder.UseProcessor(() => new NamedProcessor("third"));

        var ctx = new DefaultHttpContext();
        var produced = builder.ProcessorFactories.Select(f => (NamedProcessor)f(ctx)).ToArray();

        Assert.Equal(new[] { "first", "second", "third" }, produced.Select(p => p.Name));
    }

    private sealed class NamedProcessor : FrameProcessor
    {
        public NamedProcessor(string name) : base(name) { }
        protected override ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class TestHello { public string? Greeting { get; set; } }
}
