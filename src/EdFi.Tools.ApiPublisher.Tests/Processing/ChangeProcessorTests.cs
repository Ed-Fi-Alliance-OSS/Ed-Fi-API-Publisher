// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Reflection;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class ChangeProcessorTests
    {

        private ChangeProcessor GetChangeProcessor()
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

            var suppliedSourceResources = sourceResourceFaker.Generate(5);

            // Prepare the fake source API endpoint
            var _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                // Test-specific mocks
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 1)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

            // -----------------------------------------------------------------

            // -----------------------------------------------------------------
            //                      Target Requests
            // -----------------------------------------------------------------

            var _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            // Every POST succeeds
            _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            // -----------------------------------------------------------------
            //                  Source/Target Connection Details
            // -----------------------------------------------------------------

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                exclude: new[] { "assessments", "/ed-fi/sections", "/tpdm/candidates" });

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            // -----------------------------------------------------------------
            //                    Options and Configuration
            // -----------------------------------------------------------------

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false; // Shorten test execution time
                                                // -----------------------------------------------------------------

            // Initialize logging
            TestHelpers.InitializeLogging();
            var configurationStoreSection = null as IConfigurationSection;

            // Configuration
            var _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

            // Create change processor with dependencies
            var _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                _fakeSourceRequestHandler,
                targetApiConnectionDetails,
                _fakeTargetRequestHandler);

            return _changeProcessor;

        }

        [Test]
        public void GetKeyChangeDependenciesTest_PostDependencies_SingleResourceWithNoDependencies_DoesNotThrow()
        {
            // Arrange
            var changeProcessor = GetChangeProcessor();

            var method = typeof(ChangeProcessor).GetMethod("GetKeyChangeDependencies", BindingFlags.NonPublic | BindingFlags.Instance);

            var postDependencies = new Dictionary<string, string[]>
                                    {
                                        { "/ed-fi/assessment", Array.Empty<string>() },
                                    };

            var updatableKeys = new[] {
                "/ed-fi/classPeriods",
                "/ed-fi/grades",
                "/ed-fi/gradebookEntries",
                "/ed-fi/locations",
                "/ed-fi/sections",
                "/ed-fi/sessions",
                "/ed-fi/studentSchoolAssociations",
                "/ed-fi/studentSectionAssociations"
                 };

            // Assert
            Assert.DoesNotThrow(() =>
            {
                // Act
                var result = (IDictionary<string, string[]>)method.Invoke(changeProcessor, new object[] { postDependencies, updatableKeys });
            });

        }


        [Test]
        public void GetKeyChangeDependenciesTest_PostDependencies_SingleResourceWithNestedDependencies_DoesNotThrow()
        {
            // Arrange
            var changeProcessor = GetChangeProcessor();

            var method = typeof(ChangeProcessor).GetMethod("GetKeyChangeDependencies", BindingFlags.NonPublic | BindingFlags.Instance);

            var postDependencies = new Dictionary<string, string[]>
            {
                ["/ed-fi/accountabilityRatings"] = new[] { "/ed-fi/communityOrganizations", "/ed-fi/communityProviders", "/ed-fi/educationOrganizationNetworks", "/ed-fi/educationServiceCenters" },
            };

            var updatableKeys = new[] {
                "/ed-fi/classPeriods",
                "/ed-fi/grades",
                "/ed-fi/gradebookEntries",
                "/ed-fi/locations",
                "/ed-fi/sections",
                "/ed-fi/sessions",
                "/ed-fi/studentSchoolAssociations",
                "/ed-fi/studentSectionAssociations"
                 };


            // Assert
            Assert.DoesNotThrow(() =>
            {
                // Act
                var result = (IDictionary<string, string[]>)method.Invoke(changeProcessor, new object[] { postDependencies, updatableKeys });
            });

        }


        [Test]
        public void GetKeyChangeDependenciesTest_PostDependencies_MultipleResourcesWithComplexDependencies_DoesNotThrow()
        {
            // Arrange
            var changeProcessor = GetChangeProcessor();

            var method = typeof(ChangeProcessor).GetMethod("GetKeyChangeDependencies", BindingFlags.NonPublic | BindingFlags.Instance);

            var postDependencies = new Dictionary<string, string[]>
            {
                ["/ed-fi/academicWeeks"] = new[] { "/ed-fi/schools" },
                ["/ed-fi/accountabilityRatings"] = new[] { "/ed-fi/communityOrganizations", "/ed-fi/communityProviders", "/ed-fi/educationOrganizationNetworks", "/ed-fi/educationServiceCenters" },
                ["/ed-fi/assessmentItems"] = new[] { "/ed-fi/assessments", "/ed-fi/learningStandards" },
                ["/ed-fi/assessments"] = new[] { "/ed-fi/communityOrganizations", "/ed-fi/communityProviders", "/ed-fi/educationOrganizationNetworks", "/ed-fi/educationServiceCenters" },
                ["/ed-fi/assessmentScoreRangeLearningStandards"] = new[] { "/ed-fi/assessments", "/ed-fi/learningStandards", "/ed-fi/objectiveAssessments" },
                ["/ed-fi/balanceSheetDimensions"] = Array.Empty<string>(),
                ["/ed-fi/bellSchedules"] = new[] { "/ed-fi/classPeriods", "/ed-fi/schools" },
                ["/ed-fi/calendarDates"] = new[] { "/ed-fi/calendars" },
                ["/ed-fi/calendars"] = new[] { "/ed-fi/schools" },
                ["/ed-fi/chartOfAccounts"] = new[] { "/ed-fi/balanceSheetDimensions", "/ed-fi/communityOrganizations", "/ed-fi/communityProviders", "/ed-fi/educationOrganizationNetworks" },
                ["/ed-fi/classPeriods"] = new[] { "/ed-fi/schools" },
                ["/ed-fi/cohorts"] = new[] { "/ed-fi/communityOrganizations", "/ed-fi/communityProviders", "/ed-fi/educationOrganizationNetworks", "/ed-fi/educationServiceCenters" },
                ["/ed-fi/communityOrganizations"] = Array.Empty<string>()
            };

            var updatableKeys = new[] {
                "/ed-fi/classPeriods",
                "/ed-fi/grades",
                "/ed-fi/gradebookEntries",
                "/ed-fi/locations",
                "/ed-fi/sections",
                "/ed-fi/sessions",
                "/ed-fi/studentSchoolAssociations",
                "/ed-fi/studentSectionAssociations"
                 };


            // Assert
            Assert.DoesNotThrow(() =>
            {
                // Act
                var result = (IDictionary<string, string[]>)method.Invoke(changeProcessor, new object[] { postDependencies, updatableKeys });
            });

        }
    }
}
