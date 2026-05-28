using AtariHackerMCP.Atari;
using AtariHackerMCP.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
	options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<RomSession>();
builder.Services.AddSingleton<SymbolTable>(_ =>
{
	var table = new SymbolTable();
	AtariHardwareMap.Populate(table);
	return table;
});
builder.Services.AddSingleton<ZeroPageMap>(_ =>
{
	var map = new ZeroPageMap();
	AtariHardwareMap.PopulateZeroPage(map);
	return map;
});
builder.Services.AddSingleton<SessionPersistence>();

builder.Services
	.AddMcpServer()
	.WithStdioServerTransport()
	.WithToolsFromAssembly();

await builder.Build().RunAsync();
