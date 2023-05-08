// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Versioning;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class CurrentChangeVersionCollector : ISourceCurrentChangeVersionProvider
{
    private readonly ISourceCurrentChangeVersionProvider _currentChangeVersionProvider;
    private readonly IPublishingOperationMetadataCollector _metadataCollector;

    public CurrentChangeVersionCollector(
        ISourceCurrentChangeVersionProvider currentChangeVersionProvider,
        IPublishingOperationMetadataCollector metadataCollector)
    {
        _currentChangeVersionProvider = currentChangeVersionProvider;
        _metadataCollector = metadataCollector;
    }

    public async Task<long?> GetCurrentChangeVersionAsync()
    {
        var currentChangeVersion = await _currentChangeVersionProvider.GetCurrentChangeVersionAsync();
        
        _metadataCollector.SetCurrentChangeVersion(currentChangeVersion);

        return currentChangeVersion;
    }
}
