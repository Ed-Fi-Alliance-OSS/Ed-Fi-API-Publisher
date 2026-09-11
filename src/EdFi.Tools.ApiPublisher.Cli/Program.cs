// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using Autofac.Extensions.DependencyInjection;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;
using EdFi.Tools.ApiPublisher.Core.Modules;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Registration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly.RateLimit;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Cli
{
    internal class Program
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(Program));

        private static async Task<int> Main(string[] args)
        {
            InitializeLogging();

            _logger.Information(
                "Initializing the Ed-Fi API Publisher.");

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

                    return PublisherExitCode.InvalidConfiguration;
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

                Func<string> moduleFactory = (!string.IsNullOrWhiteSpace(options.RemediationsScriptFile))
                    ? () => File.ReadAllText(options.RemediationsScriptFile)
                    : null;

                var configurationStoreSection = finalConfiguration.GetSection("configurationStore");

                var changeProcessorConfiguration = new ChangeProcessorConfiguration(
                    options,
                    authorizationFailureHandling,
                    resourcesWithUpdatableKeys,
                    configurationStoreSection,
                    moduleFactory);

                var changeProcessor = executionContainer.Resolve<ChangeProcessor>();

                _logger.Information($"Processing started.");
                await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, cancellationToken).ConfigureAwait(false);
                _logger.Information($"Processing complete.");

                return PublisherExitCode.Success;
            }
            //catch (RateLimitRejectedException ex)
            //{
            //    _logger.Fatal(ex, ex.Message);
            //    return -1;
            //}
            catch (Exception ex)
            {
                if (Flatten(ex).Any(EdFiApiAuthenticationException.IsRepresentedBy))
                {
                    // The single most important line for an unattended run: name authentication as the cause rather
                    // than leaving it inside a generic failure message.
                    _logger.Fatal(ex, $"Processing failed because the publisher could not authenticate against the API. No further data was published.{Environment.NewLine}{DescribeExceptionChain(ex)}");

                    return PublisherExitCode.AuthenticationFailure;
                }

                // A run that finished but rejected documents is reported apart from one that broke, so that
                // an unattended caller can tell "some documents need attention" from "re-run this", and both
                // from a configuration mistake that published nothing at all (APIPUB-120).
                int exitCode = PublisherExitCode.ForFailure(ex);

                // The exception is passed to the logger, not just its rendered chain: exit code 2 means the
                // run did not complete and nobody knows why yet, which is the one outcome where the operator
                // needs the stack trace. The template is a constant so that a brace in an exception message
                // cannot be read as a property token.
                _logger.Error(
                    ex,
                    exitCode == PublisherExitCode.InvalidConfiguration
                        ? "Configuration failed:{FailureDetail:l}"
                        : "Processing failed:{FailureDetail:l}",
                    $"{Environment.NewLine}{DescribeExceptionChain(ex)}");

                return exitCode;
            }
            finally
            {
                Log.CloseAndFlush();
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

            if (options.ToleratedItemErrorCount < -1)
            {
                validationErrors.Add($"{nameof(options.ToleratedItemErrorCount)} value of '{options.ToleratedItemErrorCount}' is invalid. It must be -1 (tolerate any number of failed documents), 0 (the default: fail the run if any document fails), or a positive number.");
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

            if (options.ProcessingBlockBoundedCapacity < -1)
            {
                validationErrors.Add($"{nameof(options.ProcessingBlockBoundedCapacity)} value of '{options.ProcessingBlockBoundedCapacity}' is invalid. It must be -1 (unbounded), 0 (automatic), or a positive number.");
            }

            if (!string.IsNullOrEmpty(options.RemediationsScriptFile) && !File.Exists(options.RemediationsScriptFile))
            {
                validationErrors.Add($"{nameof(options.RemediationsScriptFile)} must be a local file path to an existing JavaScript module.");
            }

            if (options.UseChangeVersionPaging && options.ChangeVersionPagingWindowSize < 1)
            {
                validationErrors.Add($"{nameof(options.ChangeVersionPagingWindowSize)} must be greater than 0.");
            }

            if (options.CursorPagingPartitionCount is < 1 or > Options.MaxCursorPagingPartitionCount)
            {
                validationErrors.Add($"{nameof(options.CursorPagingPartitionCount)} must be between 1 and {Options.MaxCursorPagingPartitionCount}.");
            }

            if (validationErrors.Any())
            {
                throw new InvalidConfigurationException($"Options are invalid:{Environment.NewLine}{string.Join(Environment.NewLine, validationErrors)}");
            }
        }

        /// <summary>
        /// Enumerates an exception and everything nested inside it, including the individual inner exceptions
        /// of an <see cref="AggregateException" /> (a faulted resource can carry several).
        /// </summary>
        private static IEnumerable<Exception> Flatten(Exception ex)
        {
            if (ex is null)
            {
                yield break;
            }

            yield return ex;

            if (ex is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.InnerExceptions.SelectMany(Flatten))
                {
                    yield return innerException;
                }

                yield break;
            }

            foreach (var innerException in Flatten(ex.InnerException))
            {
                yield return innerException;
            }
        }

        /// <summary>
        /// Renders the exception chain as one message per line. Joining the chain with spaces produced a single
        /// run-on line that hid where one failure ended and the next began (see APIPUB-120).
        /// </summary>
        private static string DescribeExceptionChain(Exception ex)
        {
            return string.Join(
                Environment.NewLine,
                Flatten(ex)
                    .Where(exception => exception is not AggregateException)
                    .Select(exception => $"  {exception.GetType().Name}: {exception.Message}"));
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
                throw new InvalidConfigurationException($"{type} connection is not fully defined and no connection name was provided.");
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
            var configBuilder = new ConfigurationBuilder()
                .AddJsonFile("logging.json");
            var loggerConfig = configBuilder.Build();
            Log.Logger = new LoggerConfiguration()
               .ReadFrom.Configuration(loggerConfig)
               .Enrich.WithThreadId()
               .Enrich.FromLogContext()
               .CreateLogger();
        }
    }
}
