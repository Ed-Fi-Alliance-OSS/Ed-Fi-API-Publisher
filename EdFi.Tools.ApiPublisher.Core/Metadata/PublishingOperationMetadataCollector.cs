// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Concurrent;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class PublishingOperationMetadataCollector : IPublishingOperationMetadataCollector
{
    private long? _currentChangeVersion;
    private JObject _sourceVersionMetadata;
    private JObject _targetVersionMetadata;
    private readonly ConcurrentDictionary<string, long> _resourceItemCountByPath = new(StringComparer.OrdinalIgnoreCase);
    private ChangeWindow _changeWindow;

    public void SetCurrentChangeVersion(long? changeVersion)
    {
        _currentChangeVersion = changeVersion;
    }

    public void SetSourceVersionMetadata(JObject versionMetadata)
    {
        _sourceVersionMetadata = versionMetadata;
    }

    public void SetTargetVersionMetadata(JObject versionMetadata)
    {
        _targetVersionMetadata = versionMetadata;
    }

    public void SetChangeWindow(ChangeWindow changeWindow)
    {
        _changeWindow ??= changeWindow;
    }

    public void SetResourceItemCount(string resourcePath, long count)
    {
        _resourceItemCountByPath.AddOrUpdate(resourcePath, 
            _ => count, 
            (_, _) => count);
    }

    public PublishingOperationMetadata GetMetadata()
        => new()
        {
            CurrentChangeVersion = _currentChangeVersion,
            SourceVersionMetadata = _sourceVersionMetadata,
            TargetVersionMetadata = _targetVersionMetadata,
            ChangeWindow = _changeWindow,
            ResourceItemCountByPath = _resourceItemCountByPath
        };
}
