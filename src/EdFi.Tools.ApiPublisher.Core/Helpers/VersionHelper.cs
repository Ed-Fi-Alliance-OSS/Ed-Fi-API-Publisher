// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public static class VersionExtensions
    {
        public static bool IsAtLeast(this Version apiVersion, int majorVersion, int minorVersion)
        {
            return apiVersion.Major > majorVersion 
                || (apiVersion.Major == majorVersion && apiVersion.Minor >= minorVersion);
        }
    }
}
