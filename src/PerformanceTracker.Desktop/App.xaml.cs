using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Desktop.Services;
using PerformanceTracker.Infrastructure.Services;
using PerformanceTracker.Infrastructure.Storage;

namespace PerformanceTracker.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayIconService? _trayIconService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startMinimized = e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.SetBasePath(AppContext.BaseDirectory);
                configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<CollectorSettings>(context.Configuration.GetSection("Collector"));
                services.Configure<RetentionSettings>(context.Configuration.GetSection("Retention"));

                services.AddSingleton<IDiagnosisService, SimpleDiagnosisService>();
                services.AddSingleton<IEventDetector>(serviceProvider =>
                    new ThresholdEventDetector(
                        serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorSettings>>().Value,
                        serviceProvider.GetRequiredService<IDiagnosisService>()));

                services.AddSingleton<IMetricCollector, WindowsPerformanceCollector>();
                services.AddSingleton<IForegroundAppTracker, ForegroundWindowTracker>();
                services.AddSingleton<ISampleRepository>(_ =>
                {
                    string dataDirectory = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PerformanceTracker");

                    return new SqliteSampleRepository(System.IO.Path.Combine(dataDirectory, "performance-tracker.db"));
                });

                services.AddSingleton<MonitoringState>();
                services.AddSingleton<TrayIconService>();
                services.AddHostedService<MonitoringWorker>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        MainWindow mainWindow = _host.Services.GetRequiredService<MainWindow>();
        _trayIconService = _host.Services.GetRequiredService<TrayIconService>();
        _trayIconService.Initialize(
            () => mainWindow.ShowFromTray(),
            () =>
            {
                mainWindow.PrepareForExit();
                Shutdown();
            });

        MainWindow = mainWindow;
        if (!startMinimized)
        {
            mainWindow.Show();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
