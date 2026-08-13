// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Bogus;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using EdFi.Tools.ApiPublisher.Tests.Models;
using FakeItEasy;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    // Covers the --processDeletesAndKeyChangesOnFullPublish option (APIPUB-113). On a full publish
    // (change window starting at version 1 or below) delete and key change processing are skipped by
    // default; the new opt-in flag forces them to run. The change window here resolves to
    // MinChangeVersion = 1 because the source reports nothing processed to the target yet
    // (GetSourceApiConnectionDetails(0) => MinChangeVersion = 1 + 0).
    [TestFixture]
    public class ProcessDeletesAndKeyChangesOnFullPublishTests
    {
        public abstract class When_full_publishing_with_updatable_keys : TestFixtureAsyncBase
        {
            private const int TestItemQuantity = 2;

            protected ChangeProcessor _changeProcessor;
            protected ChangeProcessorConfiguration _changeProcessorConfiguration;
            protected string[] _resourcesWithUpdatableKeys;
            protected IFakeHttpRequestHandler _fakeSourceRequestHandler;
            protected IFakeHttpRequestHandler _fakeTargetRequestHandler;
            protected List<KeyChange<FakeKey>> _suppliedKeyChanges;
            protected List<GenericResource<FakeKey>> _suppliedTargetResources;

            protected abstract bool ProcessDeletesAndKeyChangesOnFullPublish { get; }

            protected override async Task ArrangeAsync()
            {
                // -----------------------------------------------------------------
                //                      Source Requests
                // -----------------------------------------------------------------
                int changeVersion = 1001;

                var keyValueFaker = TestHelpers.GetKeyValueFaker();

                var keyChangeFaker = new Faker<KeyChange<FakeKey>>().StrictMode(true)
                    .RuleFor(o => o.Id, f => Guid.NewGuid().ToString("n"))
                    .RuleFor(o => o.ChangeVersion, f => changeVersion++)
                    .Ignore(o => o.OldKeyValues)
                    .RuleFor(o => o.OldKeyValuesObject, f => keyValueFaker.Generate())
                    .Ignore(o => o.NewKeyValues)
                    .RuleFor(o => o.NewKeyValuesObject, f => keyValueFaker.Generate());

                _suppliedKeyChanges = keyChangeFaker.Generate(TestItemQuantity);

                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: TestItemQuantity)
                    .GetResourceData(@"/data/v3/ed-fi/\w+/keyChanges", _suppliedKeyChanges);

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                int i = 0;

                var targetResourceFaker = new Faker<GenericResource<FakeKey>>().StrictMode(true)
                    .RuleFor(o => o.Id, f => Guid.NewGuid().ToString("n"))
                    .RuleFor(o => o.SomeReference, f => _suppliedKeyChanges[i++].OldKeyValuesObject)
                    .RuleFor(o => o.VehicleManufacturer, f => f.Vehicle.Manufacturer())
                    .RuleFor(o => o.VehicleYear, f => f.Date.Between(DateTime.Today.AddYears(-50), DateTime.Today).Year);

                _suppliedTargetResources = targetResourceFaker.Generate(TestItemQuantity);

                _fakeTargetRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                    .SetBaseUrl(MockRequests.TargetApiBaseUrl)
                    .SetDataManagementUrlSegment(EdFiApiConstants.DataManagementApiSegment)
                    .SetChangeQueriesUrlSegment(EdFiApiConstants.ChangeQueriesApiSegment)
                    .OAuthToken()
                    .ApiVersionMetadata()
                    .Dependencies();

                for (int j = 0; j < TestItemQuantity; j++)
                {
                    _fakeTargetRequestHandler.GetResourceData(
                        @"/data/v3/ed-fi/\w+",
                        _suppliedKeyChanges[j].OldKeyValuesObject.ToQueryStringParams(),
                        new[] { _suppliedTargetResources[j] });
                }

                // -----------------------------------------------------------------
                //                  Source/Target Connection Details
                // -----------------------------------------------------------------

                // Full publish: nothing processed to the target yet, so MinChangeVersion resolves to 1.
                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(0);
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                // -----------------------------------------------------------------
                //                    Options and Configuration
                // -----------------------------------------------------------------

                var options = TestHelpers.GetOptions();
                options.ProcessDeletesAndKeyChangesOnFullPublish = ProcessDeletesAndKeyChangesOnFullPublish;

                TestHelpers.InitializeLogging();

                _resourcesWithUpdatableKeys = TestHelpers.Configuration.GetResourcesWithUpdatableKeys();

                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(
                    options,
                    resourcesWithUpdatableKeys: _resourcesWithUpdatableKeys);

                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    _fakeTargetRequestHandler);

                await Task.Yield();
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }
        }

        [TestFixture]
        public class And_the_flag_is_not_set : When_full_publishing_with_updatable_keys
        {
            protected override bool ProcessDeletesAndKeyChangesOnFullPublish => false;

            [Test]
            public void Should_skip_key_change_processing_by_default()
            {
                // The source must never be queried for keyChanges (neither the support probe nor the data GET).
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => msg.RequestUri.LocalPath.EndsWith(EdFiApiConstants.KeyChangesPathSuffix))))
                    .MustNotHaveHappened();
            }
        }

        [TestFixture]
        public class And_the_flag_is_set : When_full_publishing_with_updatable_keys
        {
            protected override bool ProcessDeletesAndKeyChangesOnFullPublish => true;

            [Test]
            public void Should_process_key_changes_from_the_source()
            {
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => msg.RequestUri.LocalPath.EndsWith(EdFiApiConstants.KeyChangesPathSuffix))))
                    .MustHaveHappened();
            }
        }

        [TestFixture]
        public class When_full_publishing_deletes_and_the_flag_is_set : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;

            protected override async Task ArrangeAsync()
            {
                var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();
                var suppliedSourceResources = sourceResourceFaker.Generate(5);

                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: 1)
                    .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                    .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

                // Full publish: nothing processed to the target yet, so MinChangeVersion resolves to 1.
                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(0);
                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false; // Shorten test execution time
                options.ProcessDeletesAndKeyChangesOnFullPublish = true;

                TestHelpers.InitializeLogging();

                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    fakeTargetRequestHandler);

                await Task.Yield();
            }

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_process_deletes_from_the_source()
            {
                A.CallTo(() => _fakeSourceRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => msg.RequestUri.LocalPath.EndsWith(EdFiApiConstants.DeletesPathSuffix))))
                    .MustHaveHappened();
            }
        }
    }
}
