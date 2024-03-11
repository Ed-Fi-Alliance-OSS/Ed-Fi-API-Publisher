// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing;

[TestFixture]
public class ProfileApplicationTests
{
    [TestFixture]
    public class When_applying_profiles_to_source_and_target_connections : TestFixtureAsyncBase
    {
        private ChangeProcessor _changeProcessor;
        private IFakeHttpRequestHandler _fakeTargetRequestHandler;
        private IFakeHttpRequestHandler _fakeSourceRequestHandler;
        private ChangeProcessorConfiguration _changeProcessorConfiguration;
        
        private const string TestWritableProfileName = "Unit-Test-Target-Profile";
        private const string TestReadableProfileName = "Unit-Test-Source-Profile";

        protected override async Task ArrangeAsync()
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

            var suppliedSourceResources = sourceResourceFaker.Generate(3);

            // Prepare the fake source API endpoint
            _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                // Test-specific mocks
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 3)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

            // -----------------------------------------------------------------
            //                      Target Requests
            // -----------------------------------------------------------------
            _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                
            // Every POST succeeds
            _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            // -----------------------------------------------------------------
            //                  Source/Target Connection Details
            // -----------------------------------------------------------------
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                includeOnly: new[]
                {
                    "students",
                    "academicSubjectDescriptors"
                },
                profileName: TestReadableProfileName);

            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails(profileName: TestWritableProfileName);

            // -----------------------------------------------------------------
            //                    Options and Configuration
            // -----------------------------------------------------------------
            // Initialize options
            var options = TestHelpers.GetOptions();
            // -----------------------------------------------------------------

            // Initialize logging
            TestHelpers.InitializeLogging();
            
            // Configuration
            _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);
            
            // Create change processor with dependencies
            _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                _fakeSourceRequestHandler,
                targetApiConnectionDetails,
                _fakeTargetRequestHandler);
        }

        protected override async Task ActAsync()
        {
            await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
        }

        [Test]
        public void Should_NOT_apply_profile_content_types_to_descriptors_GET_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/academicSubjectDescriptors",
                    A<HttpRequestMessage>.That.Matches(msg => DoesNotUseProfileContentType(msg))))
                .MustHaveHappenedTwiceExactly(); // Once for count, once for data
        }
        
        [Test]
        public void Should_NOT_apply_profile_content_types_to_descriptors_POST_requests()
        {
            A.CallTo(() => _fakeTargetRequestHandler.Post(
                    $@"{MockRequests.TargetApiBaseUrl}/{_fakeTargetRequestHandler.DataManagementUrlSegment}/ed-fi/academicSubjectDescriptors",
                    A<HttpRequestMessage>.That.Matches(msg => DoesNotUseProfileContentType(msg))))
                .MustHaveHappened(3, Times.Exactly);
        }
        
        [Test]
        public void Should_apply_readable_profile_content_type_to_count_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/students",
                    A<HttpRequestMessage>.That.Matches(msg => UsesReadableContentType(msg) && QueryStringHasTotalCount(msg.RequestUri))))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void Should_apply_readable_profile_content_type_to_all_GET_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/students",
                    A<HttpRequestMessage>.That.Matches(msg => UsesReadableContentType(msg) && !QueryStringHasTotalCount(msg.RequestUri))))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void Should_apply_writable_profile_content_type_to_all_POST_requests()
        {
            A.CallTo(() => _fakeTargetRequestHandler.Post(
                    $@"{MockRequests.TargetApiBaseUrl}/{_fakeTargetRequestHandler.DataManagementUrlSegment}/ed-fi/students",
                    A<HttpRequestMessage>.That.Matches(msg => UsesWritableContentType(msg))))
                .MustHaveHappened(3, Times.Exactly);
        }

        [Test]
        public void Should_NOT_apply_profile_content_type_to_any_deletes_requests()
        {
            A.CallTo(() => _fakeSourceRequestHandler.Get(
                    $@"{MockRequests.SourceApiBaseUrl}/{_fakeSourceRequestHandler.DataManagementUrlSegment}/ed-fi/students/deletes",
                    A<HttpRequestMessage>.That.Matches(msg => DoesNotUseProfileContentType(msg))))
                .MustHaveHappenedOnceOrMore();
        }

        private static bool DoesNotUseProfileContentType(HttpRequestMessage msg)
        {
            return !msg.Headers.Accept.ToString().StartsWith("application/vnd.ed-fi.");
        }

        private bool QueryStringHasTotalCount(Uri msgRequestUri)
        {
            return msgRequestUri?.ParseQueryString().AllKeys.Contains("totalCount", StringComparer.OrdinalIgnoreCase) ?? false;
        }
        
        private bool UsesReadableContentType(HttpRequestMessage requestMessage)
        {
            var match = Regex.Match(
                requestMessage.Headers.Accept.ToString(),
                @"application/vnd.ed-fi.(?<ResourceName>\w+).(?<ProfileName>[\w\-]+).readable\+json");

            if (!match.Success)
            {
                return false;
            }
            
            return match.Groups["ProfileName"].Value == TestReadableProfileName.ToLower();
        }

        private bool UsesWritableContentType(HttpRequestMessage requestMessage)
        {
            var match = Regex.Match(
                requestMessage.Content.Headers.ContentType.ToString(),
                @"application/vnd.ed-fi.(?<ResourceName>\w+).(?<ProfileName>[\w\-]+).writable\+json");

            if (!match.Success)
            {
                return false;
            }
            
            return match.Groups["ProfileName"].Value == TestWritableProfileName.ToLower();
        }
    }
    
    [TestFixture]
    public class When_applying_a_profile_to_the_source_and_not_to_the_target_connections
    {
        private ChangeProcessor _changeProcessor;
        private IFakeHttpRequestHandler _fakeTargetRequestHandler;
        private IFakeHttpRequestHandler _fakeSourceRequestHandler;
        private ChangeProcessorConfiguration _changeProcessorConfiguration;
        
        private const string TestReadableProfileName = "Unit-Test-Source-Profile";

        [Test]
        public async Task Should_throw_an_exception_to_prevent_data_loss()
        {
            // -----------------------------------------------------------------
            //                      Source Requests
            // -----------------------------------------------------------------
            var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

            var suppliedSourceResources = sourceResourceFaker.Generate(3);

            // Prepare the fake source API endpoint
            _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                // Test-specific mocks
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: 3)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

            // -----------------------------------------------------------------
            //                      Target Requests
            // -----------------------------------------------------------------
            _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                
            // Every POST succeeds
            _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            // -----------------------------------------------------------------
            //                  Source/Target Connection Details
            // -----------------------------------------------------------------
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                includeOnly: new[]
                {
                    "students",
                    "academicSubjectDescriptors"
                },
                profileName: TestReadableProfileName);

            // No profile name applied to the target
            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails(profileName: null);

            // -----------------------------------------------------------------
            //                    Options and Configuration
            // -----------------------------------------------------------------
            // Initialize options
            var options = TestHelpers.GetOptions();
            // -----------------------------------------------------------------

            // Initialize logging
            TestHelpers.InitializeLogging();
            
            // Configuration
            _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);
            
            Should.Throw<Exception>(
                () => 
                    // Create change processor with dependencies
                    _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                        options,
                        sourceApiConnectionDetails,
                        _fakeSourceRequestHandler,
                        targetApiConnectionDetails,
                        _fakeTargetRequestHandler))
                .Message.ShouldBe("The source API connection has a ProfileName specified, but the target API connection does not. POST requests against a target API without the Profile-based context of the source data can lead to accidental data loss.");
        }
    }
}
