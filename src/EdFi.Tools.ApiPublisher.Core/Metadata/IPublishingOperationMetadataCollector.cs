// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public interface IPublishingOperationMetadataCollector
{
    void SetCurrentChangeVersion(long? changeVersion);
    void SetSourceVersionMetadata(JObject versionMetadata);
    void SetTargetVersionMetadata(JObject versionMetadata);
    void SetChangeWindow(ChangeWindow changeWindow);
    void SetResourceItemCount(string resourcePath, long count);
    
    PublishingOperationMetadata GetMetadata();
}
