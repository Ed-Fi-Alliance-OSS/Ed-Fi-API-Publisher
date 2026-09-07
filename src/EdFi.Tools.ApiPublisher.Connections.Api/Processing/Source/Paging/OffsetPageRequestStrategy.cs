// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// Addresses page requests with the Ed-Fi ODS API's <c>offset</c>/<c>limit</c> query string parameters,
/// continuing past a final page that came back full (unless reverse paging is in use, where the producer
/// already covers the whole change window).
/// </summary>
public class OffsetPageRequestStrategy : IPageRequestStrategy
{
    private readonly ILogger _logger = Log.ForContext(typeof(OffsetPageRequestStrategy));

    public IPageRequestSequence Begin<TProcessDataMessage>(
        StreamResourcePageMessage<TProcessDataMessage> message,
        Options options)
    {
        long offset = message.Offset
            ?? throw new NullReferenceException("Offset is expected on resource page messages for the Ed-Fi ODS API.");

        int limit = message.Limit
            ?? throw new NullReferenceException("Limit is expected on resource page messages for the Ed-Fi ODS API.");

        return new Sequence(_logger, message.ResourceUrl, offset, limit, message.IsFinalPage, options.UseReversePaging);
    }

    private sealed class Sequence : IPageRequestSequence
    {
        private readonly ILogger _logger;
        private readonly string _resourceUrl;
        private readonly int _limit;
        private readonly bool _isFinalPage;
        private readonly bool _useReversePaging;
        private long _offset;

        public Sequence(
            ILogger logger,
            string resourceUrl,
            long offset,
            int limit,
            bool isFinalPage,
            bool useReversePaging)
        {
            _logger = logger;
            _resourceUrl = resourceUrl;
            _offset = offset;
            _limit = limit;
            _isFinalPage = isFinalPage;
            _useReversePaging = useReversePaging;
        }

        public string BuildQueryString() => $"?offset={_offset}&limit={_limit}";

        public string Describe() => $"{_offset} to {_offset + _limit - 1}";

        public string DescribeStart() => $"offset {_offset}";

        public bool TryAdvance(HttpResponseMessage response, int? topLevelItemCount)
        {
            // Reverse paging descends through the change window by change version, so a full page never
            // implies more data beyond the pages already produced
            if (_useReversePaging)
            {
                return false;
            }

            // Perform limit/offset final page check (for need for possible continuation)
            // (No count means no continuation)
            if (!_isFinalPage || topLevelItemCount != _limit)
            {
                return false;
            }

            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                _logger.Debug("{ResourceUrl}: Final page was full. Attempting to retrieve more data.", _resourceUrl);
            }

            // Looks like there could be more data
            _offset += _limit;

            return true;
        }
    }
}
