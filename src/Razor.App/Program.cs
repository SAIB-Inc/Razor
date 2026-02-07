using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Razor.U5C.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "RAZOR_");

var listenAddress = builder.Configuration["Grpc:ListenAddress"] ?? "http://0.0.0.0:50051";
if (!Uri.TryCreate(listenAddress, UriKind.Absolute, out var listenUri))
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

    if (IPAddress.TryParse(listenUri.Host, out var ip))
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

builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

var app = builder.Build();

app.MapGrpcService<SyncServiceImpl>();
app.MapGrpcReflectionService();

app.MapGet("/", () => "Razor U5C gRPC server is running.");

app.Run();
