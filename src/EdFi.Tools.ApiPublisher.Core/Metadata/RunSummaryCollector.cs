// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;

namespace EdFi.Tools.ApiPublisher.Core.Metadata
{
    /// <summary>
    /// Accumulates the per-resource counts behind the run summary. Written to from every processing block, so
    /// all state is either concurrent or updated through <see cref="Interlocked" />.
    /// </summary>
    public class RunSummaryCollector : IRunSummaryCollector
    {
        private readonly IPublishingOperationMetadataCollector _metadataCollector;

        private readonly ConcurrentDictionary<(PublishingStage Stage, string ResourcePath), ResourceCounters> _countersByResource =
            new();

        private readonly ConcurrentDictionary<string, byte> _skipReasons = new(StringComparer.OrdinalIgnoreCase);

        private long _sourceReadErrorCount;

        public RunSummaryCollector(IPublishingOperationMetadataCollector metadataCollector)
        {
            _metadataCollector = metadataCollector;
        }

        public void AddAttemptedItems(string sourceResourceUrl, long count)
        {
            if (count <= 0)
            {
                return;
            }

            var (stage, resourcePath) = ParseSourceResourceUrl(sourceResourceUrl);

            Interlocked.Add(ref GetCounters(stage, resourcePath).Attempted, count);
        }

        public void AddError(PublishingStage stage, ErrorItemMessage error)
        {
            // A source read failure is a page or a count that could not be read, not a rejected document: the
            // documents behind it were never attempted and their number is not known, so it is reported on its
            // own rather than as a failure of the resource. Only the source-side errors leave the item id
            // unset, which is what tells them apart from a target GET issued to locate a single document.
            if (error.Id is null && string.Equals(error.Method, HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _sourceReadErrorCount);

                return;
            }

            Interlocked.Increment(ref GetCounters(stage, NormalizeResourcePath(error.ResourceUrl)).Failed);
        }

        public void AddSkippedItems(PublishingStage stage, string resourceUrl, long count, string reason)
        {
            if (count <= 0)
            {
                return;
            }

            Interlocked.Add(ref GetCounters(stage, NormalizeResourcePath(resourceUrl)).Skipped, count);

            if (!string.IsNullOrWhiteSpace(reason))
            {
                _skipReasons.TryAdd(reason, 0);
            }
        }

        public RunSummary GetSummary()
        {
            var expectedItemCountByResource = GetExpectedItemCountByResource();

            var resourceKeys = _countersByResource.Keys
                .Concat(expectedItemCountByResource.Keys)
                .Distinct()
                .OrderBy(key => key.Stage)
                .ThenBy(key => key.ResourcePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var resources = resourceKeys
                .Select(key =>
                {
                    _countersByResource.TryGetValue(key, out var counters);

                    long? expectedItemCount = expectedItemCountByResource.TryGetValue(key, out long expected) && expected >= 0
                        ? expected
                        : null;

                    return new ResourceRunSummary(
                        key.Stage,
                        key.ResourcePath,
                        expectedItemCount,
                        counters?.ReadAttempted() ?? 0,
                        counters?.ReadFailed() ?? 0,
                        counters?.ReadSkipped() ?? 0);
                })
                .ToArray();

            return new RunSummary(
                resources,
                Interlocked.Read(ref _sourceReadErrorCount),
                _skipReasons.Keys.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        /// <summary>
        /// Splits a source resource URL into the stage it belongs to and the resource path without the stage's
        /// path suffix. The suffix is what identifies the stage at the point pages are streamed.
        /// </summary>
        private static (PublishingStage Stage, string ResourcePath) ParseSourceResourceUrl(string sourceResourceUrl)
        {
            string resourcePath = StripQueryString(sourceResourceUrl);

            if (resourcePath.TryTrimSuffix(EdFiApiConstants.DeletesPathSuffix, out string pathWithoutDeletes))
            {
                return (PublishingStage.Deletes, pathWithoutDeletes);
            }

            if (resourcePath.TryTrimSuffix(EdFiApiConstants.KeyChangesPathSuffix, out string pathWithoutKeyChanges))
            {
                return (PublishingStage.KeyChanges, pathWithoutKeyChanges);
            }

            return (PublishingStage.Upserts, resourcePath);
        }

        private static string NormalizeResourcePath(string resourceUrl)
        {
            return ParseSourceResourceUrl(resourceUrl).ResourcePath;
        }

        private static string StripQueryString(string resourceUrl)
        {
            if (string.IsNullOrEmpty(resourceUrl))
            {
                return string.Empty;
            }

            int queryStringPosition = resourceUrl.IndexOf('?');

            return queryStringPosition < 0
                ? resourceUrl
                : resourceUrl.Substring(0, queryStringPosition);
        }

        private IDictionary<(PublishingStage Stage, string ResourcePath), long> GetExpectedItemCountByResource()
        {
            var expectedItemCountByResource = new Dictionary<(PublishingStage, string), long>();

            // A count of -1 records that the source could not report one, which is carried through as "unknown"
            foreach (var kvp in _metadataCollector.GetMetadata().ResourceItemCountByPath)
            {
                expectedItemCountByResource[ParseSourceResourceUrl(kvp.Key)] = kvp.Value;
            }

            return expectedItemCountByResource;
        }

        private ResourceCounters GetCounters(PublishingStage stage, string resourcePath)
        {
            return _countersByResource.GetOrAdd((stage, resourcePath), _ => new ResourceCounters());
        }

        /// <summary>
        /// The mutable counters for one resource within one stage. The fields are public because they are
        /// updated in place through <see cref="Interlocked" />, which cannot be applied to a property.
        /// </summary>
        private class ResourceCounters
        {
            public long Attempted;

            public long Failed;

            public long Skipped;

            public long ReadAttempted() => Interlocked.Read(ref Attempted);

            public long ReadFailed() => Interlocked.Read(ref Failed);

            public long ReadSkipped() => Interlocked.Read(ref Skipped);
        }
    }
}
