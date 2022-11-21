// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Capabilities;

public interface ISourceCapabilities
{
    /// <summary>
    /// Indicates whether the data source supports retrieving key changes, using the supplied resource key (if necessary) to probe
    /// for determining the capability.
    /// </summary>
    /// <param name="probeResourceKey"></param>
    /// <returns></returns>
    Task<bool> SupportsKeyChangesAsync(string probeResourceKey);

    /// <summary>
    /// Indicates whether the data source supports retrieving the keys of deleted items, using the supplied resource key (if necessary) to probe
    /// for determining the capability.
    /// </summary>
    /// <param name="probeResourceKey"></param>
    /// <returns></returns>
    Task<bool> SupportsDeletesAsync(string probeResourceKey);
}