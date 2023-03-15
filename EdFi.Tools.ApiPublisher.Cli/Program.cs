using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;
using EdFi.Tools.ApiPublisher.Core.Modules;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Registration;
using log4net;
using log4net.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Tools.ApiPublisher.Cli
{
    internal class Program
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(Program));

        private static async Task<int> Main(string[] args)
        {
            InitializeLogging();

            _logger.Info(
                "Initializing the Ed-Fi API Publisher, designed and developed by Geoff McElhanon (geoff@edufied.com, Edufied LLC) in conjunction with Student1.");

            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            
            try
            {
                // TODO: Implement plugin architecture to find all plug-ins, supplying the initial configuration as input
                var pluginTypes = new[]
                {
                    // Connection plugins
                    typeof(Connections.Api.Plugin),
                    typeof(Connections.Sqlite.Plugin),
                    
                    // Configuration store plugins
                    typeof(ConfigurationStore.Aws.Plugin),
                    typeof(ConfigurationStore.PostgreSql.Plugin),
                    typeof(ConfigurationStore.SqlServer.Plugin),
                    typeof(ConfigurationStore.Plaintext.Plugin),
                };
                
                var plugins = pluginTypes.Select(Activator.CreateInstance).Cast<IPlugin>().ToArray();

                // Build the initial configuration, incorporating command-line arguments
                IConfigurationBuilder configBuilder = new ConfigurationBuilderFactory().Create(args);

                // Allow plugins to introduce configuration values
                foreach (IPlugin plugin in plugins)
                {
                    plugin.ApplyConfiguration(args, configBuilder);
                }

                // Build the configuration
                var initialConfiguration = configBuilder.Build();

                // Initialize the configuration container
                var configurationContainerBuilder = new ContainerBuilder();

                // Prepare NodeJS (if remediations file supplied)
                var remediationsModule = new NodeJsRemediationsModule(initialConfiguration);
                configurationContainerBuilder.RegisterModule(remediationsModule);

                IContainer configurationContainer;

                try
                {
                    configurationContainer = BuildConfigurationContainer(configurationContainerBuilder, initialConfiguration, plugins);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Configuration failed: {ex.Message}");

                    return -1;
                }

                var serviceProvider = new AutofacServiceProvider(configurationContainer);

                // TODO: How to ensure connections have been configured?
                // var connectionsConfiguration = initialConfiguration.GetSection("Connections");
                
                // if (connectionsConfiguration == null)
                // {
                //     throw new ArgumentException("Connections have not been configured.");
                // }

                // Validate initial connection configuration
                var sourceConnectionDetails = GetConnectionConfiguration(initialConfiguration, configurationContainer, "Source");
                EnsureConnectionFullyDefinedOrNamed(sourceConnectionDetails, "Source");

                var targetConnectionDetails = GetConnectionConfiguration(initialConfiguration, configurationContainer, "Target");
                EnsureConnectionFullyDefinedOrNamed(targetConnectionDetails, "Target");

                // After root container has been initialized, resolve configuration builder enhancers and enhance the configuration details
                if (sourceConnectionDetails.NeedsResolution() || targetConnectionDetails.NeedsResolution())
                {
                    _logger.Debug($"Connection details are incomplete after initial configuration. Beginning configuration enhancement processing...");
                    
                    var enhancers = serviceProvider.GetServices<IConfigurationBuilderEnhancer>();

                    foreach (var enhancer in enhancers)
                    {
                        _logger.Debug($"Running configuration builder enhancer '{enhancer.GetType().FullName}'...");
                        enhancer.Enhance(initialConfiguration, configBuilder);
                    }
                }
                
                // Build the final configuration
                var finalConfiguration = configBuilder.Build();
                
                // Prepare final runtime configuration
                // API Publisher Settings
                var publisherSettings = finalConfiguration.Get<ApiPublisherSettings>();
                
                // Validate the finalized options
                var options = publisherSettings.Options;
                _logger.Debug($"Validating configuration options...");
                ValidateOptions(options);
                
                var authorizationFailureHandling = publisherSettings.AuthorizationFailureHandling;
                var resourcesWithUpdatableKeys = publisherSettings.ResourcesWithUpdatableKeys;

                // Create child container for execution
                await using var executionContainer = configurationContainer.BeginLifetimeScope(
                    builder =>
                    {
                        builder.RegisterModule<CoreModule>();

                        // Add "default" registrations from the "core" assembly, leaving any existing registrations intact
                        // Registers types found matching the simple "default service" naming convention (Foo for IFoo)
                        builder
                            .RegisterAssemblyTypes(typeof(CoreModule).Assembly)
                            .UsingDefaultImplementationConvention();

                        builder.RegisterInstance(options);
                        
                        // Allow plugins to perform initial registrations
                        foreach (IPlugin plugin in plugins)
                        {
                            plugin.PerformFinalRegistrations(builder, finalConfiguration);
                        }
                    });

                Func<string>? moduleFactory = (!string.IsNullOrWhiteSpace(options.RemediationsScriptFile))
                    ? () => File.ReadAllText(options.RemediationsScriptFile)
                    : null as Func<string>;

                var configurationStoreSection = finalConfiguration.GetSection("configurationStore");

                var changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    resourcesWithUpdatableKeys,
                    moduleFactory,
                    options,
                    configurationStoreSection);

                var changeProcessor = executionContainer.Resolve<ChangeProcessor>();

                _logger.Info($"Processing started.");
                await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, cancellationToken).ConfigureAwait(false);
                _logger.Info($"Processing complete.");

                return 0;
            }
            catch (Exception ex)
            {
                _logger.Error($"Processing failed: {string.Join(" ", GetExceptionMessages(ex))}");
                
                return -1;
            }

            INamedConnectionDetails GetConnectionConfiguration(IConfigurationRoot initialConfiguration, IContainer rootContainer, string connectionSectionName)
            {
                var connectionConfiguration = initialConfiguration.GetSection("Connections").GetSection(connectionSectionName);
                var connectionType = connectionConfiguration.GetValue<string>("Type") ?? "api";

                var connectionDetails = rootContainer.ResolveNamed<INamedConnectionDetails>(connectionType);
                connectionConfiguration.Bind(connectionDetails);
                
                return connectionDetails;
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
            
            if (options.MaxDegreeOfParallelismForResourceProcessing < 1)
            {
                validationErrors.Add($"{nameof(options.MaxDegreeOfParallelismForResourceProcessing)} must be greater than 0.");
            }
            
            if (options.MaxDegreeOfParallelismForPostResourceItem < 1)
            {
                validationErrors.Add($"{nameof(options.MaxDegreeOfParallelismForPostResourceItem)} must be greater than 0.");
            }
            
            if (options.MaxDegreeOfParallelismForStreamResourcePages < 1)
            {
                validationErrors.Add($"{nameof(options.MaxDegreeOfParallelismForStreamResourcePages)} must be greater than 0.");
            }

            if (!string.IsNullOrEmpty(options.RemediationsScriptFile) && !File.Exists(options.RemediationsScriptFile))
            {
                validationErrors.Add($"{nameof(options.RemediationsScriptFile)} must be a local file path to an existing JavaScript module.");
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

        private static void EnsureConnectionFullyDefinedOrNamed(INamedConnectionDetails connectionDetails, string type)
        {
            // If source and target connections are fully defined, we're done
            if (connectionDetails.IsFullyDefined())
            {
                _logger.Debug($"{type} connection is fully defined.");
                return;
            }

            // Ensure that names are provided for the connection if it's not already fully defined
            if (!connectionDetails.IsFullyDefined() && string.IsNullOrEmpty(connectionDetails.Name))
            {
                throw new ArgumentException($"{type} connection is not fully defined and no connection name was provided.");
            }
        }

        private static IContainer BuildConfigurationContainer(ContainerBuilder containerBuilder, IConfigurationRoot configuration, IPlugin[] plugins)
        {
            // Allow plugins to perform initial registrations
            foreach (IPlugin plugin in plugins)
            {
                plugin.PerformConfigurationRegistrations(containerBuilder, configuration);
            }

            return containerBuilder.Build();
        }

        private static void InitializeLogging()
        {
            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            var configFile = new FileInfo("log4net.config");
            XmlConfigurator.Configure(logRepository, configFile);
        }
    }
}