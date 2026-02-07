using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Razor.Commands;
using Razor.Configuration;
using Razor.Services;

var baseConfiguration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "RAZOR_")
    .Build();

var rootCommand = new RootCommand("Razor - Cardano node implementation in .NET");
var dataPathOption = new Option<string>("--data-path")
{
    Description = "Path to Razor data directory (overrides Storage:Path)."
};
rootCommand.Options.Add(dataPathOption);

var runCommand = new Command("run", "Start the Cardano node");
runCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var serviceProvider = BuildServiceProvider(baseConfiguration, parseResult.GetValue(dataPathOption));
    var commandHandler = serviceProvider.GetRequiredService<RunCommand>();
    await commandHandler.ExecuteAsync();
});

rootCommand.Subcommands.Add(runCommand);

var queryCommand = new Command("query", "Query information from the blockchain");
rootCommand.Subcommands.Add(queryCommand);

var queryTipCommand = new Command("tip", "Query the current blockchain tip");
queryTipCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var serviceProvider = BuildServiceProvider(baseConfiguration, parseResult.GetValue(dataPathOption));
    var commandHandler = serviceProvider.GetRequiredService<QueryCommand>();
    await commandHandler.QueryTipAsync();
});
queryCommand.Subcommands.Add(queryTipCommand);

var queryUtxosByAddressCommand = new Command("utxos", "Query UTXOs for a specific address");
var addressOption = new Option<string>("--address")
{
    Description = "Cardano address to query",
    Required = true
};
queryUtxosByAddressCommand.Options.Add(addressOption);
queryUtxosByAddressCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var serviceProvider = BuildServiceProvider(baseConfiguration, parseResult.GetValue(dataPathOption));
    var commandHandler = serviceProvider.GetRequiredService<QueryCommand>();
    var address = parseResult.GetRequiredValue(addressOption);
    await commandHandler.QueryUtxosByAddressAsync(address);
});
queryCommand.Subcommands.Add(queryUtxosByAddressCommand);

var transactionCommand = new Command("transaction", "Transaction related commands");
rootCommand.Subcommands.Add(transactionCommand);

var sumbitTransactionCommand = new Command("submit", "Submit transaction to the network");
var txCborHexArg = new Argument<string>("tx cbor hex")
{
    Description = "Cardano transaction CBOR hex",
    Arity = ArgumentArity.ExactlyOne
};
sumbitTransactionCommand.Arguments.Add(txCborHexArg);
sumbitTransactionCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var commandHandler = serviceProvider.GetRequiredService<TransactionCommand>();
    var txCborHex = parseResult.GetRequiredValue(txCborHexArg);
    await commandHandler.SubmitTransactionAsync(txCborHex);
});
transactionCommand.Subcommands.Add(sumbitTransactionCommand);

return rootCommand.Parse(args).Invoke();

static IServiceProvider BuildServiceProvider(IConfiguration baseConfiguration, string? dataPathOverride)
{
    var configBuilder = new ConfigurationBuilder().AddConfiguration(baseConfiguration);
    if (!string.IsNullOrWhiteSpace(dataPathOverride))
    {
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Path"] = dataPathOverride
        });
    }

    var configuration = configBuilder.Build();

    var services = new ServiceCollection();
    ConfigureServices(services, configuration);

    return services.BuildServiceProvider();
}

static void ConfigureServices(ServiceCollection services, IConfiguration configuration)
{
    services.ConfigureLogging();
    services.Configure<NodeConfiguration>(configuration.GetSection("NodeConfiguration"));
    services.Configure<StorageConfiguration>(configuration.GetSection("Storage"));

    services.AddSingleton<NodeService>();
    services.AddTransient<RunCommand>();
    services.AddTransient<QueryCommand>();
    services.AddTransient<TransactionCommand>();
}
