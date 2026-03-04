using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Razor.Core.Storage;
using Razor.Core.Sync;
using Razor.Storage;
using Razor.Sync;
using Razor.U5C.Sync;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

_ = builder.Configuration.AddEnvironmentVariables(prefix: "RAZOR_");
_ = builder.Logging.AddFilter("Grpc.AspNetCore.Server", LogLevel.Warning);
_ = builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning);
_ = builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
_ = builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);

string listenAddress = builder.Configuration["Grpc:ListenAddress"] ?? "http://0.0.0.0:50051";
if (!Uri.TryCreate(listenAddress, UriKind.Absolute, out Uri? listenUri))
{
    throw new InvalidOperationException("Grpc:ListenAddress must be an absolute URI (e.g. http://0.0.0.0:50051).");
}

builder.WebHost.ConfigureKestrel(options =>
{
    void ConfigureListen(Action<ListenOptions> configure)
    {
        options.ListenAnyIP(listenUri.Port, configure);
    }

    if (listenUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
    {
        options.ListenLocalhost(listenUri.Port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
        });
        return;
    }

    if (IPAddress.TryParse(listenUri.Host, out IPAddress? ip))
    {
        options.Listen(ip, listenUri.Port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
        });
        return;
    }

    ConfigureListen(listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

_ = builder.Services.AddGrpc();
_ = builder.Services.AddGrpcReflection();

string dataPath = builder.Configuration["Storage:Path"] ?? "./data";
_ = builder.Services.AddSingleton<IBlockStore>(_ => new ZoneTreeBlockStore(dataPath));
_ = builder.Services.AddSingleton<ChainEventHub>();
_ = builder.Services.AddSingleton<IChainEventSource>(sp => sp.GetRequiredService<ChainEventHub>());
builder.Services.Configure<ChainSyncOptions>(builder.Configuration.GetSection("Sync"));

string shelleyGenesisPath = builder.Configuration["Sync:ShelleyGenesisPath"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "shelley-genesis.json");
if (!File.Exists(shelleyGenesisPath))
{
    throw new FileNotFoundException($"Shelley genesis file not found at '{shelleyGenesisPath}'. Set Sync:ShelleyGenesisPath or place shelley-genesis.json in the working directory.");
}
_ = builder.Services.AddSingleton(GenesisConfig.LoadFromShelleyGenesis(shelleyGenesisPath));

_ = builder.Services.AddHostedService<ChainSyncIndexer>();

WebApplication app = builder.Build();

_ = app.MapGrpcService<SyncServiceHandler>();
_ = app.MapGrpcReflectionService();

_ = app.MapGet("/", () => "Razor U5C gRPC server is running.");

app.Run();
