using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using EdFi.Tools.ApiPublisher.Core._Installers;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;
using log4net.Config;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Cli
{
    internal class Program
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Program));

        private static async Task<int> Main(string[] args)
        {
            InitializeLogging();

            Logger.Info(
                "Initializing the Ed-Fi API Publisher, designed and developed by Geoff McElhanon (geoffrey@mcelhanon.com, Edufied LLC) in conjunction with Student1.");

            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            
            try
            {
                var configBuilder = new ConfigurationBuilderFactory()
                    .CreateConfigurationBuilder(args);
                
                var initialConfiguration = configBuilder.Build();
                var configurationStoreSection = initialConfiguration.GetSection("configurationStore");
                
                // Validate initial connection configuration
                var connections = initialConfiguration.Get<ConnectionConfiguration>().Connections;
                ValidateInitialConnectionConfiguration(connections);

                // Initialize the container
                var container = new WindsorContainer();

                try
                {
                    InitializeContainer(container, configurationStoreSection);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Configuration failed: {ex.Message}");

                    return -1;
                }

                // After container has been initialized, now enhance the configuration builder
                if (connections.Source.NeedsResolution() || connections.Target.NeedsResolution())
                {
                    var enhancers = container.ResolveAll<IConfigurationBuilderEnhancer>();

                    foreach (var enhancer in enhancers)
                    {
                        enhancer.Enhance(configBuilder);
                    }
                }
                
                // Build the final configuration
                var finalConfiguration = configBuilder.Build();
                
                // Prepare final runtime configuration
                // API Publisher Settings
                var publisherSettings = finalConfiguration.Get<ApiPublisherSettings>();
                
                var options = publisherSettings.Options;

                ValidateOptions(options);
                
                var authorizationFailureHandling = publisherSettings.AuthorizationFailureHandling;
                var resourcesWithUpdatableKeys = publisherSettings.ResourcesWithUpdatableKeys;

                var apiConnections = finalConfiguration.Get<ConnectionConfiguration>().Connections;
                
                // Initialize source/target API clients
                var sourceApiConnectionDetails = apiConnections.Source;
                var targetApiConnectionDetails = apiConnections.Target;
                
                EdFiApiClient CreateSourceApiClient() => new EdFiApiClient("Source", sourceApiConnectionDetails, options.BearerTokenRefreshMinutes, options.IgnoreSSLErrors);
                EdFiApiClient CreateTargetApiClient() => new EdFiApiClient("Target", targetApiConnectionDetails, options.BearerTokenRefreshMinutes, options.IgnoreSSLErrors);
                
                var changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    resourcesWithUpdatableKeys,
                    sourceApiConnectionDetails,
                    targetApiConnectionDetails,
                    CreateSourceApiClient,
                    CreateTargetApiClient,
                    options,
                    finalConfiguration.GetSection("configurationStore"));

                var changeProcessor = container.Resolve<IChangeProcessor>();

                Logger.Info($"Processing started.");
                await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, cancellationToken).ConfigureAwait(false);
                Logger.Info($"Processing complete.");

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"Processing failed: {string.Join(" ", GetExceptionMessages(ex))}");
                
                return -1;
            }
        }

        private static void ValidateOptions(Options options)
        {
            var validationErrors = new List<string>();
            
            if (options.MaxRetryAttempts < 0)
            {
                validationErrors.Add($"{nameof(options.MaxRetryAttempts)} cannot be a negative number.");
            }
            
            if (options.StreamingPageSize < 1)
            {
                validationErrors.Add($"{nameof(options.StreamingPageSize)} must be greater than 0.");
            }

            if (options.BearerTokenRefreshMinutes < 1)
            {
                validationErrors.Add($"{nameof(options.BearerTokenRefreshMinutes)} must be greater than 0.");
            }

            if (options.ErrorPublishingBatchSize < 1)
            {
                validationErrors.Add($"{nameof(options.ErrorPublishingBatchSize)} must be greater than 0.");
            }

            if (options.RetryStartingDelayMilliseconds < 1)
            {
                validationErrors.Add($"{nameof(options.RetryStartingDelayMilliseconds)} must be greater than 0.");
            }
            
            if (options.StreamingPagesWaitDurationSeconds < 1)
            {
                validationErrors.Add($"{nameof(options.StreamingPagesWaitDurationSeconds)} must be greater than 0.");
            }
            
            if (options.MaxDegreeOfParallelismForPostResourceItem < 1)
            {
                validationErrors.Add($"{nameof(options.MaxDegreeOfParallelismForPostResourceItem)} must be greater than 0.");
            }
            
            if (options.MaxDegreeOfParallelismForStreamResourcePages < 1)
            {
                validationErrors.Add($"{nameof(options.MaxDegreeOfParallelismForStreamResourcePages)} must be greater than 0.");
            }

            if (validationErrors.Any())
            {
                throw new Exception($"Options are invalid:{Environment.NewLine}{string.Join(Environment.NewLine, validationErrors)}");
            }
        }

        private static IEnumerable<string> GetExceptionMessages(Exception ex)
        {
            var currentException = ex;

            while (currentException != null)
            {
                yield return currentException.Message;

                currentException = currentException.InnerException;
            }
        }

        private static void ValidateInitialConnectionConfiguration(Connections connections)
        {
            // Ensure connections have been configured
            if (connections == null)
            {
                throw new ArgumentException("Connections have not been configured.");
            }
            
            // If source and target connections are fully defined, we're done
            if (connections.Source.IsFullyDefined() && connections.Target.IsFullyDefined())
            {
                Logger.Debug($"Source and target API connections are fully defined. No named connections are being used.");
                return;
            }

            // Ensure that names are provided for API connections that are not already fully defined
            if (!connections.Source.IsFullyDefined() && string.IsNullOrEmpty(connections.Source.Name))
            {
                throw new ArgumentException("Source API connection is not fully defined and no connection name was provided.");
            }

            if (!connections.Target.IsFullyDefined() && string.IsNullOrEmpty(connections.Target.Name))
            {
                throw new ArgumentException("Target API connection is not fully defined and no connection name was provided.");
            }
        }

        private static IWindsorContainer InitializeContainer(
            IWindsorContainer container,
            IConfigurationSection configurationStoreSection)
        {
            container.Install(new EdFiToolsApiPublisherCoreInstaller());
            
            InstallApiConnectionConfigurationSupport(container, configurationStoreSection);

            return container;
        }

        private static void InstallApiConnectionConfigurationSupport(
            IWindsorContainer container,
            IConfigurationSection configurationStoreSection)
        {
            EnsureEdFiAssembliesLoaded();

            string configurationSourceName = configurationStoreSection.GetValue<string>("provider");
            
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