// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Versioning;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public class SourceEdFiVersionMetadataCollector : ISourceEdFiApiVersionMetadataProvider
{
    private readonly ISourceEdFiApiVersionMetadataProvider _sourceEdFiApiVersionMetadataProvider;
    private readonly IPublishingOperationMetadataCollector _metadataCollector;

    public SourceEdFiVersionMetadataCollector(
        ISourceEdFiApiVersionMetadataProvider sourceEdFiApiVersionMetadataProvider,
        IPublishingOperationMetadataCollector metadataCollector)
    {
        _sourceEdFiApiVersionMetadataProvider = sourceEdFiApiVersionMetadataProvider;
        _metadataCollector = metadataCollector;
    }

    public async Task<JObject> GetVersionMetadata()
    {
        var versionMetadata = await _sourceEdFiApiVersionMetadataProvider.GetVersionMetadata();

        _metadataCollector.SetSourceVersionMetadata(versionMetadata);

        return versionMetadata;
    }
}
