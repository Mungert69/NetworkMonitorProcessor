using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NetworkMonitor.Connection;
using NetworkMonitor.Objects.Factory;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Processor.Services;
using NetworkMonitor.Utils.Helpers;
using NetworkMonitor.Objects;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Collections.Generic;
using NetworkMonitor.Security;

namespace NetworkMonitor.Processor
{
    class Program
    {
#pragma warning disable CS8618
        private static ConnectFactory _connectFactory;
        private static ICmdProcessorProvider _cmdProcessorProvider;
        private static MonitorPingProcessor _monitorPingProcessor;
#pragma warning restore CS8618

        static async Task Main(string[] args)
        {
            Console.WriteLine("Start");
            IConfiguration config;
            string stateDirAppSettings = "./state/appsettings.json";

            if (File.Exists(stateDirAppSettings))
            {
                config = new ConfigurationBuilder()
                    .AddJsonFile(stateDirAppSettings, optional: false, reloadOnChange: false)
                    .AddEnvironmentVariables()
                    .AddCommandLine(args)
                    .Build();
            }
            else
            {
                config = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                    .AddEnvironmentVariables()
                    .AddCommandLine(args)
                    .Build();
            }

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Information)
                    .AddFilter("System", LogLevel.Information)
                    .AddFilter("Program", LogLevel.Debug)
                    .AddSimpleConsole(options =>
                    {
                        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                        options.IncludeScopes = true;
                    });
            });

            var logger = loggerFactory.CreateLogger<Program>();
            var envPath = config["EnvPath"];
            if (string.IsNullOrWhiteSpace(envPath))
            {
                envPath = Path.Combine("./state", ".env");
            }

            var envStore = new EnvFileStore(envPath, loggerFactory.CreateLogger<EnvFileStore>());
            envStore.LoadIntoProcess();
           // GetConfigHelper.Initialize(config, loggerFactory.CreateLogger<GetConfigHelper>());

            string appDataDirectory;
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            {
                appDataDirectory = "";
            }
            else
            {
                appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            var netConfig = new NetConnectConfig(config, appDataDirectory);
            var fileRepo = new FileRepo(true, "./state");
            var protectedConfigManager = new ProtectedConfigManager(config, envStore, fileRepo, loggerFactory.CreateLogger<ProtectedConfigManager>());
            await protectedConfigManager.SynchronizeSensitiveValuesAsync(netConfig, ProtectedConfigurationParameters.All).ConfigureAwait(false);


            // Seed default state
            fileRepo.CheckFileExistsWithCreateStringJsonZObject("ProcessorDataObj", new ProcessorDataObj(), logger);
            fileRepo.CheckFileExistsWithCreateJsonZObject("MonitorIPs", new List<MonitorIP>(), logger);
            fileRepo.CheckFileExistsWithCreateJsonZObject("PingParams", new PingParams
            {
                Timeout = 59000,
                AlertThreshold = 4,
                HostLimit = 10
            }, logger);

            var processorStates = new LocalProcessorStates();

            IRabbitRepo rabbitRepo = new RabbitRepo(loggerFactory.CreateLogger<RabbitRepo>(), netConfig);
            var resultRabbitRepo = await rabbitRepo.ConnectAndSetUp();
            if (!resultRabbitRepo.Success)
            {
                logger.LogError(resultRabbitRepo.Message);
                return;
            }
            logger.LogInformation(resultRabbitRepo.Message);

            // --- Web automation singletons ---
            ILaunchHelper launchHelper = new LaunchHelper();

            // NEW: one BrowserHost for the whole process
            IBrowserHost browserHost = new BrowserHost(
                launchHelper,
                netConfig,
                loggerFactory.CreateLogger<BrowserHost>()
            );
            // ---------------------------------

            _cmdProcessorProvider = new CmdProcessorProvider(
               loggerFactory,
               rabbitRepo,
               netConfig,
               browserHost
           );
            var resultCmdProcessorProvider = await _cmdProcessorProvider.Setup();

            // ConnectFactory (keep as-is unless you updated its ctor to also accept IBrowserHost)
            _connectFactory = new NetworkMonitor.Connection.ConnectFactory(
                loggerFactory.CreateLogger<ConnectFactory>(),
                netConfig: netConfig,
                cmdProcessorProvider: _cmdProcessorProvider,
                 browserHost: browserHost
            );

            _ = _connectFactory.SetupChromium(netConfig);

            _monitorPingProcessor = new MonitorPingProcessor(
                loggerFactory.CreateLogger<MonitorPingProcessor>(),
                netConfig,
                _connectFactory,
                fileRepo,
                rabbitRepo,
                processorStates,
                protectedConfigManager
            );

            IRabbitListener rabbitListener = new RabbitListener(
                _monitorPingProcessor,
                loggerFactory.CreateLogger<RabbitListener>(),
                netConfig,
                processorStates,
                _cmdProcessorProvider
            );

            var resultListener = await rabbitListener.Setup();
            if (!resultListener.Success)
            {
                logger.LogError(resultListener.Message);
                return;
            }
            logger.LogInformation(resultListener.Message);

            var result = await _monitorPingProcessor.Init(new ProcessorInitObj());
            if (!result.Success)
            {
                logger.LogError(result.Message);
                return;
            }
            logger.LogInformation(result.Message);

            processorStates.IsSetup = result.Success;

            if (config["AuthDevice"] == "true")
            {
                var authService = new AuthService(
                    loggerFactory.CreateLogger<AuthService>(),
                    netConfig,
                    rabbitRepo,
                    processorStates
                );
                await authService.InitializeAsync();
                await authService.SendAuthRequestAsync();
                await authService.PollForTokenAsync();
            }

            await Task.Delay(-1);

#if !ANDROID
            Console.CancelKeyPress += async (o, e) =>
            {
                Console.WriteLine("Exit");
                await _monitorPingProcessor.OnStoppingAsync();
            };
#endif
        }

    }
}
