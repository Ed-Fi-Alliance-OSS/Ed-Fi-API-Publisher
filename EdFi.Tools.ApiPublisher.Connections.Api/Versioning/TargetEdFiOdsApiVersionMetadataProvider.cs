// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public class TargetEdFiOdsApiVersionMetadataProvider : EdFiOdsApiVersionMetadataProviderBase, ITargetEdFiOdsApiVersionMetadataProvider
{
    public TargetEdFiOdsApiVersionMetadataProvider(ITargetEdFiApiClientProvider targetEdFiApiClientProvider)
        : base("Target", targetEdFiApiClientProvider) { }
}
