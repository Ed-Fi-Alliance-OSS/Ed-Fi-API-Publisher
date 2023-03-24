// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
    public static class ApiRequestHelper
    {
        public static string GetChangeWindowQueryStringParameters(ChangeWindow changeWindow)
        {
            string changeWindowParms = changeWindow == null
                ? String.Empty
                : $"&minChangeVersion={changeWindow.MinChangeVersion}&maxChangeVersion={changeWindow.MaxChangeVersion}";
            
            return changeWindowParms;
        }
    }
}
