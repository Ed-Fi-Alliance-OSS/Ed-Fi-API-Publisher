// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public interface ISourceConnectionDetails : INamedConnectionDetails
{
    public bool? IgnoreIsolation { get; set; }

    IDictionary<string, long> LastChangeVersionProcessedByTargetName { get; }
        
    public string Include { get; set; }
        
    public string IncludeOnly { get; set; }
        
    public string Exclude { get; set; }
        
    public string ExcludeOnly { get; set; }
        
    /// <summary>
    /// Gets or sets the explicitly provided value to use for the last change version processed.
    /// </summary>
    // TODO: Should this property really be here? Perhaps it should be more of a contextual argument somewhere else.
    long? LastChangeVersionProcessed { get; set; }
}
