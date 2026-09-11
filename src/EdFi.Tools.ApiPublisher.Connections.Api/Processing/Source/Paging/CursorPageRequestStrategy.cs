// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// Addresses page requests with the ODS/API 7.3+ <c>pageToken</c>/<c>pageSize</c> query string parameters and walks
/// a partition by following the <c>Next-Page-Token</c> response header, which is present on every non-empty page
/// (short pages included) and absent only on an empty page (see APIPUB-136/APIPUB-139). Never emits
/// <c>offset</c>, <c>limit</c> or <c>totalCount</c>, and always sends <c>pageSize</c>.
/// </summary>
public class CursorPageRequestStrategy : IPageRequestStrategy
{
    public const string NextPageTokenHeader = "Next-Page-Token";

    private readonly ILogger _logger = Log.ForContext(typeof(CursorPageRequestStrategy));

    public IPageRequestSequence Begin<TProcessDataMessage>(
        StreamResourcePageMessage<TProcessDataMessage> message,
        Options options)
    {
        string pageToken = message.PageToken
            ?? throw new NullReferenceException("PageToken is expected on cursor-paged resource page messages for the Ed-Fi ODS API.");

        int pageSize = message.PageSize
            ?? throw new NullReferenceException("PageSize is expected on cursor-paged resource page messages for the Ed-Fi ODS API.");

        message.PartitionPageNumber = 1;

        // The message travels with every item the walk produces (DescribeSourcePage is the item error's source
        // locator), so the sequence keeps its token and page ordinal current as the walk advances
        return new Sequence(
            _logger,
            message.ResourceUrl,
            pageToken,
            pageSize,
            message.PartitionIndex,
            (currentToken, currentPageNumber) =>
            {
                message.PageToken = currentToken;
                message.PartitionPageNumber = currentPageNumber;
            });
    }

    private sealed class Sequence : IPageRequestSequence
    {
        private readonly ILogger _logger;
        private readonly string _resourceUrl;
        private readonly int _pageSize;
        private readonly int? _partitionIndex;
        private readonly Action<string, int> _advanced;
        private string _pageToken;
        private int _pageNumber = 1;

        public Sequence(ILogger logger, string resourceUrl, string pageToken, int pageSize, int? partitionIndex, Action<string, int> advanced)
        {
            _logger = logger;
            _resourceUrl = resourceUrl;
            _pageToken = pageToken;
            _pageSize = pageSize;
            _partitionIndex = partitionIndex;
            _advanced = advanced;
        }

        // The token is an opaque value from the API. The ODS/API emits base64url, which escaping leaves untouched.
        // Escaping guards against a source that emits characters with query-string meaning: a hash sign would
        // truncate the query on the wire and silently drop the change window, and a plus sign would arrive as a space.
        public string BuildQueryString() => $"?pageToken={Uri.EscapeDataString(_pageToken)}&pageSize={_pageSize}";

        public string Describe() => $"of {DescribePartition()}, page {_pageNumber}";

        public string DescribeStart() => $"{DescribePartition()}, page {_pageNumber}";

        public ILogger EnrichLogger(ILogger logger)
            => logger
                .ForContext("PageToken", _pageToken)
                .ForContext("PageSize", _pageSize)
                .ForContext("PartitionIndex", _partitionIndex)
                .ForContext("PartitionPage", _pageNumber);

        public bool TryAdvance(HttpResponseMessage response, int? topLevelItemCount)
        {
            if (!response.Headers.TryGetValues(NextPageTokenHeader, out var values))
            {
                return false;
            }

            string nextPageToken = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            if (nextPageToken is null)
            {
                return false;
            }

            if (nextPageToken == _pageToken)
            {
                // A source that echoes the token it was sent would serve the same page forever (this is what a
                // pre-7.3 style endpoint that ignores pageToken looks like) -- stop rather than loop
                _logger.Warning(
                    "{ResourceUrl}: Source returned the same {Header} value it was sent ({PageToken}). Ending the partition walk to avoid re-reading the page.",
                    _resourceUrl, NextPageTokenHeader, _pageToken);

                return false;
            }

            _pageToken = nextPageToken;
            _pageNumber++;
            _advanced(_pageToken, _pageNumber);

            return true;
        }

        private string DescribePartition()
            => _partitionIndex.HasValue ? $"partition {_partitionIndex.Value}" : $"page token {_pageToken}";
    }
}
