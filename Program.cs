using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using SQL_MCP;

if (ManifestService.HandleDiscoveryArgs(args))
    return;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>()
      .GetSection(nameof(ServerSettings))
      .Get<ServerSettings>() ?? new ServerSettings());

await builder.Build().RunAsync();
