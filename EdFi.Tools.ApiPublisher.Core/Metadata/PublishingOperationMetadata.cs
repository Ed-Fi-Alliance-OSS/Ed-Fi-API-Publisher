// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public record PublishingOperationMetadata
{
    public long? CurrentChangeVersion { get; set; }
    public JObject SourceVersionMetadata { get; set; }
    public JObject TargetVersionMetadata { get; set; }
    public ChangeWindow ChangeWindow { get; set; }
    public IReadOnlyDictionary<string, long> ResourceItemCountByPath { get; set; }
}
