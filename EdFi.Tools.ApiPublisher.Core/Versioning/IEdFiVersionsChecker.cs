// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public interface IEdFiVersionsChecker
{
    /// <summary>
    /// Checks Ed-Fi API and Standard versions for compatibility
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task CheckApiVersionsAsync(ChangeProcessorConfiguration configuration);
}
