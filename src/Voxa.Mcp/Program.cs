using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Voxa MCP server (VDX-002): exposes the keyless local speech tier as MCP tools over stdio, so any
// MCP-aware agent (Claude Code, Cursor, …) can speak and transcribe with a voice you own.
var builder = Host.CreateApplicationBuilder(args);

// MCP stdio uses stdout for the JSON-RPC protocol — route ALL logging to stderr so a stray log line
// can never corrupt the message stream.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
