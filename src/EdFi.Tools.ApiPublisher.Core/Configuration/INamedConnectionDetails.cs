// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

/// <summary>
/// Defines properties and behaviors required for a source or target connection. 
/// </summary>
public interface INamedConnectionDetails
{
    /// <summary>
    /// The name to be used for uniquely identifying external configuration information for the connection and/or to track
    /// the current state of change processing for specific source and target connections.
    /// </summary>
    string Name { get; set; }

    /// <summary>
    /// Indicates whether the named connection has been fully defined by initial configuration (or requires additional
    /// augmentation from and external configuration source).
    /// </summary>
    /// <returns></returns>
    bool IsFullyDefined();

    /// <summary>
    /// Indicates that the connection is named, and needs additional resolution to the configuration from an external source. 
    /// </summary>
    /// <returns><b>true</b> if the connection configuration needs additional information; otherwise <b>false</b>.</returns>
    public bool NeedsResolution();
}
