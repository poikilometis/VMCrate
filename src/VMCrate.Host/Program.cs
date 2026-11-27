using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using VMCrate.Core;
using VMCrate.Host;
using VMCrate.Windows;

if (args is not [("--check" or "--serve"), var configuration])
{
    Console.Error.WriteLine("Usage: VMCrate.Host --check|--serve <host.json>");
    return 2;
}

try
{
    var settings = QueueSettings.Read(configuration);
    if (args[0] == "--check")
    {
        Console.WriteLine($"Configuration valid · {settings.Accounts.Length} accounts · {settings.ListenAddress}:{settings.Port}");
        return 0;
    }

    using var certificate = CertificatePins.FindPrivate(settings.CertificateSha256, CertificatePins.ServerAuthentication);
    var readiness = await new PowerShellHypervisor().ReadHostReadinessAsync();
    if (readiness.HyperV != HyperVReadiness.Ready)
    {
        Console.Error.WriteLine($"Hyper-V unavailable · {readiness.HyperV} · {readiness.Detail}");
        return 2;
    }

    using var ledger = new JobLedger(settings.QueueDirectory);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = [],
        ContentRootPath = AppContext.BaseDirectory,
        EnvironmentName = Environments.Production
    });
    builder.Configuration.Sources.Clear();
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.AddServerHeader = false;
        server.Limits.MaxRequestBodySize = 8192;
        server.Limits.MaxConcurrentConnections = 32;
        server.Listen(settings.ListenAddress, settings.Port, endpoint => endpoint.UseHttps(tls =>
        {
            tls.ServerCertificate = certificate;
            tls.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            tls.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            tls.CheckCertificateRevocation = true;
            tls.ClientCertificateValidation = (client, _, errors) =>
                errors == SslPolicyErrors.None && settings.ForCertificate(client) is not null;
        }));
    });
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(10));
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton(ledger);
    builder.Services.AddHostedService(provider =>
        new QueueWorker(settings, ledger, provider.GetRequiredService<ILogger<QueueWorker>>()));
    await using var app = builder.Build();
    QueueEndpoints.Map(app, settings, ledger);
    await app.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
