// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Reflection;

namespace EdFi.Tools.ApiPublisher.Tests.Resources
{
	public static class TestData
    {
        public static class Dependencies
        {
            // ReSharper disable once InconsistentNaming
            public static string GraphML()
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("EdFi.Tools.ApiPublisher.Tests.Resources.Dependencies-GraphML-v5.2.xml");

                using var sr = new StreamReader(stream);

                return sr.ReadToEnd();
            }
        }
    }
}
