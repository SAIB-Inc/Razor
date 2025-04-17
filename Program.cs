using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Razor.Commands;
using Razor.Configuration;
using Razor.Services;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var services = new ServiceCollection();
ConfigureServices(services, configuration);
var serviceProvider = services.BuildServiceProvider();

var rootCommand = new RootCommand("Razor - Cardano node implementation in .NET");

var runCommand = new Command("run", "Start the Cardano node");
runCommand.SetHandler(async () =>
{
    var commandHandler = serviceProvider.GetRequiredService<RunCommand>();
    await commandHandler.ExecuteAsync();
});

rootCommand.AddCommand(runCommand);

var queryCommand = new Command("query", "Query information from the blockchain");
rootCommand.AddCommand(queryCommand);

var queryTipCommand = new Command("tip", "Query the current blockchain tip");
queryTipCommand.SetHandler(async () =>
{
    var commandHandler = serviceProvider.GetRequiredService<QueryCommand>();
    await commandHandler.QueryTipAsync();
});
queryCommand.AddCommand(queryTipCommand);

var queryUtxosByAddressCommand = new Command("utxos", "Query UTXOs for a specific address");
var addressOption = new Option<string>("--address", "Cardano address to query") { IsRequired = true };
queryUtxosByAddressCommand.AddOption(addressOption);
queryUtxosByAddressCommand.SetHandler(async address =>
{
    var commandHandler = serviceProvider.GetRequiredService<QueryCommand>();
    await commandHandler.QueryUtxosByAddressAsync(address);
}, addressOption);
queryCommand.AddCommand(queryUtxosByAddressCommand);

var transactionCommand = new Command("transaction", "Transaction related commands");
rootCommand.AddCommand(transactionCommand);

var sumbitTransactionCommand = new Command("submit", "Submit transaction to the network");
var txCborHexArg = new Argument<string>("tx cbor hex", "Cardano address to query") { Arity = ArgumentArity.ExactlyOne };
sumbitTransactionCommand.AddArgument(txCborHexArg);
sumbitTransactionCommand.SetHandler(async txCborHex =>
{
    var commandHandler = serviceProvider.GetRequiredService<TransactionCommand>();
    await commandHandler.SubmitTransactionAsync(txCborHex);
}, txCborHexArg);
transactionCommand.AddCommand(sumbitTransactionCommand);

return await rootCommand.InvokeAsync(args);

static void ConfigureServices(ServiceCollection services, IConfiguration configuration)
{
    services.ConfigureLogging();
    services.Configure<NodeConfiguration>(configuration.GetSection("NodeConfiguration"));

    services.AddSingleton<NodeService>();
    services.AddTransient<RunCommand>();
    services.AddTransient<QueryCommand>();
    services.AddTransient<TransactionCommand>();
}