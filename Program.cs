using InverterTelegram;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
    })
    .ConfigureAppConfiguration(configuration =>
    {
        configuration.AddUserSecrets<Worker>();
    })
    .Build();

await host.RunAsync();
