using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Bogus;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Tests.Models;
using EdFi.Tools.ApiPublisher.Tests.Processing;
using FakeItEasy;
using log4net;
using log4net.Config;
using log4net.Repository;

namespace EdFi.Tools.ApiPublisher.Tests.Helpers
{
    public class TestHelpers
    {
        public static Faker<GenericResource<FakeKey>> GetGenericResourceFaker()
        {
            var keyValueFaker = GetKeyValueFaker();

            // Initialize a generator for a generic resource with a reference containing the key values
            var genericResourceFaker = new Faker<GenericResource<FakeKey>>().StrictMode(true)
                .RuleFor(o => o.Id, f => Guid.NewGuid().ToString("n"))
                .RuleFor(o => o.SomeReference, f => keyValueFaker.Generate())
                .RuleFor(o => o.VehicleManufacturer, f => f.Vehicle.Manufacturer())
                .RuleFor(o => o.VehicleYear, f => f.Date.Between(DateTime.Today.AddYears(-50), DateTime.Today).Year);

            return genericResourceFaker;
        }

        public static Faker<FakeKey> GetKeyValueFaker()
        {
            // Initialize a generator for the fake natural key class
            var keyValueFaker = new Faker<FakeKey>().StrictMode(true)
                .RuleFor(o => o.Name, f => f.Name.FirstName())
                .RuleFor(o => o.RetirementAge, f => f.Random.Int(50, 75))
                .RuleFor(o => o.BirthDate, f => f.Date.Between(DateTime.Today.AddYears(-75), DateTime.Today.AddYears(5)).Date);

            return keyValueFaker;
        }

        public static Options GetOptions()
        {
            return new Options
            {
                IncludeDescriptors = true,
                MaxRetryAttempts = 2,
                StreamingPageSize = 50,
                BearerTokenRefreshMinutes = 27,
                ErrorPublishingBatchSize = 50,
                RetryStartingDelayMilliseconds = 1,
                IgnoreSSLErrors = true,
                StreamingPagesWaitDurationSeconds = 10,
                MaxDegreeOfParallelismForPostResourceItem = 1,
                MaxDegreeOfParallelismForStreamResourcePages = 1,
                WhatIf = false,
            };
        }

        public static ApiConnectionDetails GetSourceApiConnectionDetails(
            int lastVersionProcessedToTarget = 1000,
            string[] resources = null,
            string[] skipResources = null,
            bool ignoreIsolation = false)
        {
            return new ApiConnectionDetails
            {
                Name = "TestSource",
                Url = MockRequests.SourceApiBaseUrl,
                Key = "sourceKey",
                Secret = "secret",
                Scope = null,

                Resources = resources == null ? null : string.Join(",", resources),
                ExcludeResources = null,
                SkipResources = skipResources == null ? null : string.Join(",", skipResources),
                
                IgnoreIsolation = ignoreIsolation,
                
                // LastChangeVersionProcessed = null,
                // LastChangeVersionsProcessed = "{ 'TestTarget': 1234 }",
                TreatForbiddenPostAsWarning = true,
                LastChangeVersionProcessedByTargetName =
                {
                    { "TestTarget", lastVersionProcessedToTarget },
                },
            };
        }

        public static ApiConnectionDetails GetTargetApiConnectionDetails()
        {
            return new ApiConnectionDetails
            {
                Name = "TestTarget",
                Url = MockRequests.TargetApiBaseUrl,
                Key = "targetKey",
                Secret = "secret",
                Scope = null,

                Resources = null, // "abc,def,ghi",
                ExcludeResources = null,
                SkipResources = null,
                
                IgnoreIsolation = true,
                
                LastChangeVersionProcessed = null,
                LastChangeVersionsProcessed = null,
                TreatForbiddenPostAsWarning = true,
                LastChangeVersionProcessedByTargetName = {},
            };
        }

        public static class Configuration
        {
            public static string[] GetResourcesWithUpdatableKeys()
            {
                return new[]
                {
                    "/ed-fi/classPeriods",
                    "/ed-fi/grades",
                    "/ed-fi/gradebookEntries",
                    "/ed-fi/locations",
                    "/ed-fi/sections",
                    "/ed-fi/sessions",
                    "/ed-fi/studentSchoolAssociations",
                    "/ed-fi/studentSectionAssociations",
                };
            }

            public static AuthorizationFailureHandling[] GetAuthorizationFailureHandling()
            {
                return new []
                {
                    new AuthorizationFailureHandling
                    {
                        Path = "/ed-fi/students",
                        UpdatePrerequisitePaths = new[] { "/ed-fi/studentSchoolAssociations" }
                    },
                    new AuthorizationFailureHandling
                    {
                        Path = "/ed-fi/staffs",
                        UpdatePrerequisitePaths = new[]
                        {
                            "/ed-fi/staffEducationOrganizationEmploymentAssociations",
                            "/ed-fi/staffEducationOrganizationAssignmentAssociations"
                        }
                    },
                    new AuthorizationFailureHandling
                    {
                        Path = "/ed-fi/parents",
                        UpdatePrerequisitePaths = new[] { "/ed-fi/studentParentAssociations" }
                    }
                };
            }
        }

        public static async Task<ILoggerRepository> InitializeLogging()
        {
            var ms = new MemoryStream();
            var sw = new StreamWriter(ms);

            const string logLevel = "INFO";
            
            await sw.WriteLineAsync(
                $@"
<log4net>
    <appender name=""ConsoleAppender"" type=""log4net.Appender.ConsoleAppender"">
        <layout type=""log4net.Layout.PatternLayout"">
            <conversionPattern value=""%date [%thread] %-5level %logger [%property{{NDC}}] - %message%newline"" />
        </layout>
    </appender>
    <appender name=""MemoryAppender"" type=""log4net.Appender.MemoryAppender"">
        <onlyFixPartialEventData value=""true"" />
    </appender>
    <root>
        <level value=""{logLevel}"" />
        <appender-ref ref=""ConsoleAppender"" />
        <appender-ref ref=""MemoryAppender"" />
    </root>
    <logger name=""EdFi.Tools.ApiPublisher.Core.Processing.Blocks.PostResource"">
        <level value=""DEBUG"" />
    </logger>
    <logger name=""EdFi.Tools.ApiPublisher.Core.Processing.Blocks.StreamResourcePages"">
        <level value=""DEBUG"" />
    </logger>
</log4net>");

            await sw.FlushAsync();
            ms.Position = 0;

            var hierarchy = LogManager.GetRepository(Assembly.GetExecutingAssembly());
            XmlConfigurator.Configure(hierarchy, ms);

            var _logger = LogManager.GetLogger(typeof(KeyChangesTests));
            _logger.Debug("Test logging initialized.");

            return hierarchy;
        }

        public static IFakeHttpRequestHandler GetFakeBaselineSourceApiRequestHandler()
        {
            return A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken()
                .ApiVersionMetadata()
                .SnapshotsEmpty()
                .LegacySnapshotsNotFound();
        }

        public static IFakeHttpRequestHandler GetFakeBaselineTargetApiRequestHandler()
        {
            return A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.TargetApiBaseUrl)
                .OAuthToken()
                .ApiVersionMetadata()
                .Dependencies();
        }
    }
}