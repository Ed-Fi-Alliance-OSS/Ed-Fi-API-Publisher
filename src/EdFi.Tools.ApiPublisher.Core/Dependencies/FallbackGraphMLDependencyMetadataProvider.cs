// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Threading.Tasks;
using System.Xml.Linq;
using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Dependencies;

public class FallbackGraphMLDependencyMetadataProvider : IGraphMLDependencyMetadataProvider
{
    private readonly Options _options;

    public FallbackGraphMLDependencyMetadataProvider(Options options)
    {
        _options = options;
    }
    
    public Task<(XElement, XNamespace)> GetDependencyMetadataAsync()
    {
        if (!_options.UseSourceDependencyMetadata)
        {
            throw new NotImplementedException(
                "The target connection does not support dependency metadata and the option to use the source connection for dependency metadata was not selected.");
        }

        throw new NotImplementedException("The source connection does not support providing dependency metadata.");
    }
}
