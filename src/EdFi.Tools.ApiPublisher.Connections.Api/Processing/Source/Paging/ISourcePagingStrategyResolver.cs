// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// Decides, once per resource, whether its pages are read with offset/limit or with partitioned cursor paging.
/// </summary>
public interface ISourcePagingStrategyResolver
{
    Task<SourcePagingStrategy> ResolveAsync(string resourceUrl, Options options);
}
