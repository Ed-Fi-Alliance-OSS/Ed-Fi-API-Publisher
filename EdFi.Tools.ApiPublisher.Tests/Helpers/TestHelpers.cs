using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Bogus;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
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
            var linkValueFaker = GetLinkValueFaker();
            
            // Initialize a generator for the fake natural key class
            var keyValueFaker = new Faker<FakeKey>().StrictMode(true)
                .RuleFor(o => o.Name, f => f.Name.FirstName())
                .RuleFor(o => o.RetirementAge, f => f.Random.Int(50, 75))
                .RuleFor(o => o.BirthDate, f => f.Date.Between(DateTime.Today.AddYears(-75), DateTime.Today.AddYears(5)).Date)
                .RuleFor(o => o.Link, f => linkValueFaker.Generate());

            return keyValueFaker;
        }

        public static Faker<Link> GetLinkValueFaker(string href = null, string rel = "Some")
        {
            var linkFaker = new Faker<Link>().StrictMode(true)
                .RuleFor(o => o.Rel, () => rel)
                .RuleFor(o => o.Href, f => href ?? $"/ed-fi/somethings/{Guid.NewGuid():n}");

            return linkFaker;
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
                MaxDegreeOfParallelismForResourceProcessing = 2,
                MaxDegreeOfParallelismForPostResourceItem = 1,
                MaxDegreeOfParallelismForStreamResourcePages = 1,
                WhatIf = false,
            };
        }

        public static ApiConnectionDetails GetSourceApiConnectionDetails(
            int lastVersionProcessedToTarget = 1000,
            string[] include = null,
            string[] includeOnly = null,
            string[] exclude = null,
            string[] excludeOnly = null,
            bool ignoreIsolation = false,
            int? schoolYear = null)
        {
            return new ApiConnectionDetails
            {
                Name = "TestSource",
                Url = MockRequests.SourceApiBaseUrl,
                Key = "sourceKey",
                Secret = "secret",
                Scope = null,
                SchoolYear = schoolYear,
               
                Include = include == null ? null : string.Join(",", include),
                IncludeOnly = includeOnly == null ? null : string.Join(",", includeOnly),
                Exclude = exclude == null ? null : string.Join(",", exclude),
                ExcludeOnly = excludeOnly == null ? null : string.Join(",", excludeOnly),

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

        public static ApiConnectionDetails GetTargetApiConnectionDetails(int? schoolYear = null)
        {
            return new ApiConnectionDetails
            {
                Name = "TestTarget",
                Url = MockRequests.TargetApiBaseUrl,
                Key = "targetKey",
                Secret = "secret",
                Scope = null,
                SchoolYear = schoolYear,

                Include = null, // "abc,def,ghi",
                Exclude = null,
                ExcludeOnly = null,
                
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

            // const string logLevel = "INFO";
            
            await sw.WriteLineAsync(
                $@"
<log4net>
    <appender name=""ConsoleAppender"" type=""log4net.Appender.ConsoleAppender"">
        <layout type=""log4net.Layout.PatternLayout"">
            <conversionPattern value=""%date [%thread] %-5level %logger [%property{{NDC}}] - %message%newline"" />
        </layout>
        <filter type=""log4net.Filter.LevelRangeFilter"">
            <levelMin value=""INFO"" />
            <levelMax value=""FATAL"" />
        </filter>
    </appender>
    <appender name=""MemoryAppender"" type=""log4net.Appender.MemoryAppender"">
        <onlyFixPartialEventData value=""true"" />
    </appender>
    <appender name=""FileAppender"" type=""log4net.Appender.FileAppender"">
        <file value=""C:\ProgramData\Ed-Fi-API-Publisher\Ed-Fi-API-Publisher-Tests.log"" />
        <appendToFile value=""false"" />
        <layout type=""log4net.Layout.PatternLayout"">
            <conversionPattern value=""%date [%thread] %-5level %logger [%property{{NDC}}] - %message%newline"" />
        </layout>
    </appender>
    <root>
        <level value=""DEBUG"" />
        <appender-ref ref=""ConsoleAppender"" />
        <appender-ref ref=""MemoryAppender"" />
        <appender-ref ref=""FileAppender"" />
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

        public static IFakeHttpRequestHandler GetFakeBaselineSourceApiRequestHandler(
                string dataManagementUrlSegment = EdFiApiConstants.DataManagementApiSegment,
                string changeQueriesUrlSegment = EdFiApiConstants.ChangeQueriesApiSegment)
        {
            return A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .SetDataManagementUrlSegment(dataManagementUrlSegment)
                .SetChangeQueriesUrlSegment(changeQueriesUrlSegment)
                .OAuthToken()
                .ApiVersionMetadata()
                .Snapshots(new []{ new Snapshot { Id = Guid.NewGuid(), SnapshotIdentifier = "ABC123", SnapshotDateTime = DateTime.Now } })
                .LegacySnapshotsNotFound();
        }

        public static IFakeHttpRequestHandler GetFakeBaselineTargetApiRequestHandler(
            string dataManagementUrlSegment = EdFiApiConstants.DataManagementApiSegment,
            string changeQueriesUrlSegment = EdFiApiConstants.ChangeQueriesApiSegment)
        {
            return A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.TargetApiBaseUrl)
                .SetDataManagementUrlSegment(dataManagementUrlSegment)
                .SetChangeQueriesUrlSegment(changeQueriesUrlSegment)
                .OAuthToken()
                .ApiVersionMetadata()
                .Dependencies();
        }
    }
}