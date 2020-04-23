using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using EdFi.Tools.ApiPublisher.Core._Installers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;
using log4net.Config;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Cli
{
    internal class Program
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Program));

        private static int Main(string[] args)
        {
            InitializeLogging();

            Logger.Info(
                "Initializing the Ed-Fi API Publisher, designed and developed by Geoff McElhanon (Edufied LLC) in conjunction with Student1.");

            var container = new WindsorContainer();

            try
            {
                InitializeContainer(container);
            }
            catch (Exception ex)
            {
                Logger.Error($"Configuration failed: {ex.Message}");
                return -1;
            }

            try
            {
                // Prepare runtime configuration
                var changeProcessorConfiguration = container.Resolve<IChangeProcessorConfigurationProvider>()
                    .GetRuntimeConfiguration(args);

                var changeProcessor = container.Resolve<IChangeProcessor>();

                Logger.Info($"Processing started.");

                changeProcessor.ProcessChanges(changeProcessorConfiguration);

                Logger.Info($"Processing complete.");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"Processing failed: {ex.Message}");
                return -1;
            }
        }

        private static IWindsorContainer InitializeContainer(IWindsorContainer container)
        {
            container.Install(new EdFiToolsApiPublisherCoreInstaller());
            InstallApiConnectionConfigurationSupport(container);

            return container;
        }

        private static void InstallApiConnectionConfigurationSupport(IWindsorContainer container)
        {
            EnsureEdFiAssembliesLoaded();
            
            string configurationSourceName = container.Resolve<IAppSettingsConfigurationProvider>()
                .GetConfiguration()
                .GetValue<string>("apiConnectionsConfigurationSource");

            var installerType = FindApiConnectionConfigurationInstallerType();

            if (installerType == null)
            {
                throw new Exception($"Unable to find installer for API connection configuration source '{configurationSourceName}'.");
            }

            // Install chosen support for API connection configuration
            var installer = (IWindsorInstaller) Activator.CreateInstance(installerType);
            container.Install(installer);

            // Ensure all Ed-Fi API Publisher assemblies are loaded
            void EnsureEdFiAssembliesLoaded()
            {
                var directoryInfo = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            
                foreach (FileInfo fileInfo in directoryInfo.GetFiles("EdFi*.dll"))
                {
                    Assembly.LoadFrom(fileInfo.FullName);
                }
            }
            
            // Search for the installer for the chosen configuration source
            Type FindApiConnectionConfigurationInstallerType()
            {
                var locatedInstallerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetExportedTypes())
                    .Where(t => t.GetInterfaces().Any(i => i == typeof(IWindsorInstaller)))
                    .FirstOrDefault(t => t.GetCustomAttributes<ApiConnectionsConfigurationSourceNameAttribute>()
                        .FirstOrDefault()
                        ?.Name.Equals(configurationSourceName, StringComparison.OrdinalIgnoreCase) == true);
                
                return locatedInstallerType;
            }
        }

        private static void InitializeLogging()
        {
            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            var configFile = new FileInfo("log4net.config");
            XmlConfigurator.Configure(logRepository, configFile);
        }
    }
}