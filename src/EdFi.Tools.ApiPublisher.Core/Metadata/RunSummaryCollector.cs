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
using System.Threading;

namespace EdFi.Tools.ApiPublisher.Core.Metadata
{
    /// <summary>
    /// Accumulates the per-resource counts behind the run summary. Written to from every processing block, so
    /// all state is either concurrent or updated through <see cref="Interlocked" />. It holds counters only:
    /// nothing here grows with the number of documents published.
    /// </summary>
    public class RunSummaryCollector : IRunSummaryCollector
    {
        private static readonly IEqualityComparer<(PublishingStage Stage, string ResourcePath)> _resourceKeyComparer =
            new ResourceKeyComparer();

        private readonly IPublishingOperationMetadataCollector _metadataCollector;

        private readonly ConcurrentDictionary<(PublishingStage Stage, string ResourcePath), ResourceCounters> _countersByResource =
            new(_resourceKeyComparer);

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

        public void AddPublishedItems(
            PublishingStage stage,
            string resourceUrl,
            long count,
            bool isAuthorizationRetryPass = false)
        {
            if (count <= 0)
            {
                return;
            }

            var counters = GetCounters(stage, StripStageSuffix(resourceUrl));

            Interlocked.Add(
                ref isAuthorizationRetryPass ? ref counters.RetryPassPublished : ref counters.Published,
                count);
        }

        public void AddError(PublishingStage stage, ErrorItemMessage error)
        {
            // A source read failure is a page or a count that could not be read, not a rejected document: the
            // documents behind it were never attempted and their number is not known, so it is reported on its
            // own rather than as a failure of the resource. The producer says so explicitly, because inferring
            // it from the method and a missing item id misread three of the paths that reach here: a SQLite
            // page failure, a delete whose source item carried no id, and any future source-side error that
            // starts populating the id.
            if (error.IsSourceReadError)
            {
                Interlocked.Increment(ref _sourceReadErrorCount);

                return;
            }

            var counters = GetCounters(stage, StripStageSuffix(error.ResourceUrl));

            // Added rather than incremented because an error can stand for a whole page of documents: the
            // SQLite target writes a page at a time and reports how many documents it carried.
            Interlocked.Add(
                ref error.IsAuthorizationRetryPass ? ref counters.RetryPassFailed : ref counters.Failed,
                error.ItemCount);
        }

        public void AddSkippedItems(
            PublishingStage stage,
            string resourceUrl,
            long count,
            string reason,
            bool isAuthorizationRetryPass = false)
        {
            if (count <= 0)
            {
                return;
            }

            var counters = GetCounters(stage, StripStageSuffix(resourceUrl));

            Interlocked.Add(
                ref isAuthorizationRetryPass ? ref counters.RetryPassSkipped : ref counters.Skipped,
                count);

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
                .Distinct(_resourceKeyComparer)
                .OrderBy(key => key.Stage)
                .ThenBy(key => key.ResourcePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var resources = resourceKeys
                .Select(key => BuildResourceSummary(key, expectedItemCountByResource))
                .ToArray();

            return new RunSummary(
                resources,
                Interlocked.Read(ref _sourceReadErrorCount),
                _skipReasons.Keys.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private ResourceRunSummary BuildResourceSummary(
            (PublishingStage Stage, string ResourcePath) key,
            IDictionary<(PublishingStage, string), long> expectedItemCountByResource)
        {
            _countersByResource.TryGetValue(key, out var counters);

            long? expectedItemCount = expectedItemCountByResource.TryGetValue(key, out long expected) && expected >= 0
                ? expected
                : null;

            if (counters is null)
            {
                return new ResourceRunSummary(key.Stage, key.ResourcePath, expectedItemCount, 0, 0, 0, 0);
            }

            long retryPassPublished = counters.Read(ref counters.RetryPassPublished);
            long retryPassFailed = counters.Read(ref counters.RetryPassFailed);
            long retryPassSkipped = counters.Read(ref counters.RetryPassSkipped);

            long firstPassFailed = counters.Read(ref counters.Failed);

            // The authorization retry pass re-publishes every document of the resource, so when it ran, what
            // it reports is what became of those documents; the first pass's failures are kept alongside to
            // show what the retry recovered (see APIPUB-120).
            bool retryPassRan = retryPassPublished > 0 || retryPassFailed > 0 || retryPassSkipped > 0;

            return new ResourceRunSummary(
                key.Stage,
                key.ResourcePath,
                expectedItemCount,
                counters.Read(ref counters.Attempted),
                retryPassRan ? retryPassFailed : firstPassFailed,
                retryPassRan ? retryPassSkipped : counters.Read(ref counters.Skipped),
                retryPassRan ? retryPassPublished : counters.Read(ref counters.Published),
                retryPassRan
                    ? new AuthorizationRetryPassSummary(
                        firstPassFailed,
                        retryPassPublished + retryPassFailed + retryPassSkipped,
                        retryPassFailed)
                    : null);
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

        private static string StripStageSuffix(string resourceUrl)
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
            var expectedItemCountByResource =
                new Dictionary<(PublishingStage, string), long>(_resourceKeyComparer);

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
        /// Compares resource keys the way the rest of the pipeline does: the stage exactly, and the resource
        /// path without regard to case, so that attempted items, errors, skips and the source's item counts
        /// all land on the same row.
        /// </summary>
        private sealed class ResourceKeyComparer : IEqualityComparer<(PublishingStage Stage, string ResourcePath)>
        {
            public bool Equals(
                (PublishingStage Stage, string ResourcePath) x,
                (PublishingStage Stage, string ResourcePath) y)
            {
                return x.Stage == y.Stage
                    && string.Equals(x.ResourcePath, y.ResourcePath, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode((PublishingStage Stage, string ResourcePath) key)
            {
                return HashCode.Combine(key.Stage, StringComparer.OrdinalIgnoreCase.GetHashCode(key.ResourcePath));
            }
        }

        /// <summary>
        /// The mutable counters for one resource within one stage, held once for the pass that reads the
        /// resource and once for the authorization retry pass that republishes it. The fields are public
        /// because they are updated in place through <see cref="Interlocked" />, which cannot be applied to
        /// a property.
        /// </summary>
        private class ResourceCounters
        {
            public long Attempted;

            public long Failed;

            public long Skipped;

            public long Published;

            public long RetryPassFailed;

            public long RetryPassSkipped;

            public long RetryPassPublished;

            public long Read(ref long counter) => Interlocked.Read(ref counter);
        }
    }
}
