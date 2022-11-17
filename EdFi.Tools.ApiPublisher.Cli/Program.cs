using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleSystemsManagement.Model.Internal.MarshallTransformations;
using Autofac;
using Autofac.Core;
using Autofac.Extensions.DependencyInjection;
using EdFi.Ods.Api.Helpers;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Isolation;
using EdFi.Tools.ApiPublisher.Core.Modules;
using EdFi.Tools.ApiPublisher.Core.NodeJs;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Registration;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Jering.Javascript.NodeJS;
using log4net;
using log4net.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Module = Autofac.Module;

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
                var configBuilder = new ConfigurationBuilderFactory()
                    .CreateConfigurationBuilder(args);
                
                // Build the initial configuration, incorporating command-line arguments
                var initialConfiguration = configBuilder.Build();
                
                // Validate initial connection configuration
                var connections = initialConfiguration.Get<ConnectionConfiguration>().Connections;
                ValidateInitialConnectionConfiguration(connections);

                // Initialize the container
                var containerBuilder = new ContainerBuilder();

                // Prepare NodeJS (if remediations file supplied)
                var remediationsModule = new NodeJsRemediationsModule(initialConfiguration);
                containerBuilder.RegisterModule(remediationsModule);

                IContainer rootContainer;

                try
                {
                    var configurationStoreSection = initialConfiguration.GetSection("configurationStore");
                    rootContainer = InitializeRootContainer(containerBuilder, configurationStoreSection);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Configuration failed: {ex.Message}");

                    return -1;
                }

                var serviceProvider = new AutofacServiceProvider(rootContainer);

                // After root container has been initialized, resolve configuration builder enhancers and enhance the configuration details
                if (connections.Source.NeedsResolution() || connections.Target.NeedsResolution())
                {
                    _logger.Debug($"API connection details are incomplete from initial configuration. Beginning configuration enhancement processing...");
                    
                    var enhancers = serviceProvider.GetServices<IConfigurationBuilderEnhancer>();

                    foreach (var enhancer in enhancers)
                    {
                        _logger.Debug($"Running configuration builder enhancer '{enhancer.GetType().FullName}'...");
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
                var executionContainer = rootContainer.BeginLifetimeScope(
                    builder =>
                    {
                        // ------------------------------------------------------------------------------------
                        //  Ed-Fi API as SOURCE
                        // ------------------------------------------------------------------------------------
                        // Register source and target EdFiApiClients
                        builder.RegisterModule(new EdFiOdsApiAsDataSourceModule(finalConfiguration));

                        // Available ChangeVersions for Source API
                        builder.RegisterType<EdFiOdsApiSourceCurrentChangeVersionProvider>()
                            .As<ISourceCurrentChangeVersionProvider>()
                            .SingleInstance();
                        
                        // Version metadata for a Source API
                        builder.RegisterType<SourceEdFiOdsApiVersionMetadataProvider>()
                            .As<ISourceEdFiOdsApiVersionMetadataProvider>()
                            .SingleInstance();

                        // Snapshot Isolation applicator for Source API
                        builder.RegisterType<EdFiOdsApiSourceIsolationApplicator>()
                            .As<ISourceIsolationApplicator>()
                            .SingleInstance();
                        
                        // Determine data source capabilities for Source API
                        builder.RegisterType<EdFiOdsApiDataSourceCapabilities>()
                            .As<IDataSourceCapabilities>()
                            .SingleInstance();

                        // Register resource page message producer using a limit/offset paging strategy
                        builder.RegisterType<EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer>()
                            .As<IStreamResourcePageMessageProducer>()
                            .SingleInstance();

                        // Register handler to perform page-based requests against a Source API
                        builder.RegisterType<EdFiOdsApiStreamResourcePageMessageHandler>()
                            .As<IStreamResourcePageMessageHandler>()
                            .SingleInstance();

                        // Register Data Source Total Count provider for Source API
                        builder.RegisterType<EdFiOdsApiDataSourceTotalCountProvider>()
                            .As<IEdFiDataSourceTotalCountProvider>()
                            .SingleInstance();
                        // ------------------------------------------------------------------------------------

                        // ------------------------------------------------------------------------------------
                        //  Ed-Fi API as TARGET
                        // ------------------------------------------------------------------------------------
                        // Version metadata for a Target API
                        builder.RegisterType<TargetEdFiOdsApiVersionMetadataProvider>()
                            .As<ITargetEdFiOdsApiVersionMetadataProvider>()
                            .SingleInstance();

                        // API dependency metadata from Ed-Fi ODS API (using Target API)
                        builder.RegisterType<IGraphMLDependencyMetadataProvider>()
                            .As<EdFiOdsApiGraphMLDependencyMetadataProvider>()
                            .WithParameter(
                                // Configure to use with Target API
                                new ResolvedParameter(
                                    (pi, ctx) => pi.ParameterType == typeof(IEdFiApiClientProvider),
                                    (pi, ctx) => ctx.Resolve<ITargetEdFiApiClientProvider>()));

                        // Register source and target EdFiApiClients
                        builder.RegisterModule(new EdFiOdsApiAsDataSinkModule(finalConfiguration));
                        // ------------------------------------------------------------------------------------

                        // TODO: This abstraction needs work. It parses JSON to an array, and produces individual action items using a factory method passed along on the page-level message
                        builder.RegisterType<EdFiOdsApiTargetItemActionMessageProducer>()
                            .As<IItemActionMessageProducer>()
                            .SingleInstance();
                        
                        // Block factories
                        builder.RegisterType<StreamResourceBlockFactory>().SingleInstance();
                        builder.RegisterType<StreamResourcePagesBlockFactory>().SingleInstance();

                        builder.RegisterType<ChangeResourceKeyBlocksFactory>().SingleInstance();
                        builder.RegisterType<PostResourceBlocksFactory>().SingleInstance();
                        builder.RegisterType<DeleteResourceBlocksFactory>().SingleInstance();

                        builder.RegisterType<PublishErrorsBlocksFactory>().SingleInstance();
                    });
                
                Func<string> moduleFactory = null;

                if (!string.IsNullOrWhiteSpace(options.RemediationsScriptFile))
                {
                    moduleFactory = () => File.ReadAllText(options.RemediationsScriptFile);
                }

                var configurationSection = finalConfiguration.GetSection("configurationStore");

                var changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    authorizationFailureHandling,
                    resourcesWithUpdatableKeys,
                    moduleFactory,
                    options,
                    configurationSection);

                var changeProcessor = executionContainer.Resolve<IChangeProcessor>();

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
                _logger.Debug($"Source and target API connections are fully defined. No named connections are being used.");
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

        private static IContainer InitializeRootContainer(
            ContainerBuilder containerBuilder,
            IConfigurationSection configurationStoreSection)
        {
            containerBuilder.RegisterModule<EdFiToolsApiPublisherCoreModule>();

            // NOTE: Consider a plugin model here
            InstallApiConnectionConfigurationSupport(containerBuilder, configurationStoreSection);

            // Add "default" registrations from the "core" assembly, leaving any existing registrations intact
            // Registers types found matching the simple "default service" naming convention (Foo for IFoo)
            containerBuilder
                .RegisterAssemblyTypes(typeof(EdFiToolsApiPublisherCoreModule).Assembly)
                .UsingDefaultImplementationConvention();

            return containerBuilder.Build();
        }

        private static void InstallApiConnectionConfigurationSupport(
            ContainerBuilder containerBuilder,
            IConfigurationSection configurationStoreSection)
        {
            EnsureEdFiAssembliesLoaded();

            string configurationSourceName = configurationStoreSection.GetValue<string>("provider");
            
            _logger.Debug($"Configuration store provider is '{configurationSourceName}'...");
            
            var moduleType = FindApiConnectionConfigurationModuleType();

            if (moduleType == null)
            {
                throw new Exception($"Unable to find an installer for API connection configuration source '{configurationSourceName}'.");
            }

            // Install chosen support for API connection configuration
            var module = (Module?) Activator.CreateInstance(moduleType);

            if (module == null)
            {
                throw new Exception($"Unable to create the installer module '{moduleType.Name}' for connection configuration source '{configurationSourceName}'.");
            }
            
            _logger.Debug($"Registering configuration store provider module '{moduleType.FullName}'...");
            
            containerBuilder.RegisterModule(module);

            // Ensure all Ed-Fi API Publisher assemblies are loaded
            void EnsureEdFiAssembliesLoaded()
            {
                var directoryInfo = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            
                foreach (FileInfo fileInfo in directoryInfo.GetFiles("EdFi*.dll"))
                {
                    _logger.Debug($"Ensuring that assembly '{fileInfo.Name}' is loaded...");
                    Assembly.LoadFrom(fileInfo.FullName);
                }
            }

            // Search for the installer for the chosen configuration source
            Type? FindApiConnectionConfigurationModuleType()
            {
                var locatedModuleType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetExportedTypes())
                    .Where(t => t.GetInterfaces().Any(i => i == typeof(IModule)))
                    .FirstOrDefault(t => t.GetCustomAttributes<ApiConnectionsConfigurationSourceNameAttribute>()
                        .FirstOrDefault()
                        ?.Name.Equals(configurationSourceName, StringComparison.OrdinalIgnoreCase) == true);
                
                return locatedModuleType;
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