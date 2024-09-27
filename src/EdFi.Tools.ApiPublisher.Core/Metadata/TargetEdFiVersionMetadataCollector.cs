// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Versioning;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class TargetEdFiVersionMetadataCollector : ITargetEdFiApiVersionMetadataProvider
{
    private readonly ITargetEdFiApiVersionMetadataProvider _targetEdFiApiVersionMetadataProvider;
    private readonly IPublishingOperationMetadataCollector _metadataCollector;

    public TargetEdFiVersionMetadataCollector(
        ITargetEdFiApiVersionMetadataProvider targetEdFiApiVersionMetadataProvider,
        IPublishingOperationMetadataCollector metadataCollector)
    {
        _targetEdFiApiVersionMetadataProvider = targetEdFiApiVersionMetadataProvider;
        _metadataCollector = metadataCollector;
    }

    public async Task<JObject> GetVersionMetadata()
    {
        var versionMetadata = await _targetEdFiApiVersionMetadataProvider.GetVersionMetadata();

        _metadataCollector.SetTargetVersionMetadata(versionMetadata);

        return versionMetadata;
    }
}
