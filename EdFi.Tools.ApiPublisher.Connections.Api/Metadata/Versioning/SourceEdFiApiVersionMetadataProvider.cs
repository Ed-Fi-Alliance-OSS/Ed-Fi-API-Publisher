// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Versioning;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;

public class SourceEdFiApiVersionMetadataProvider : EdFiApiVersionMetadataProviderBase, ISourceEdFiApiVersionMetadataProvider
{
    public SourceEdFiApiVersionMetadataProvider(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider)
        : base("Source", sourceEdFiApiClientProvider) { }
}
