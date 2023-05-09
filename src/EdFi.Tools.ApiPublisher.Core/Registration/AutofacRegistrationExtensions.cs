// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using Autofac;
using Autofac.Builder;
using Autofac.Features.Scanning;

namespace EdFi.Tools.ApiPublisher.Core.Registration
{
    public static class AutofacRegistrationExtensions
    {
        public static IRegistrationBuilder<object, ScanningActivatorData, DynamicRegistrationStyle> UsingDefaultImplementationConvention(this IRegistrationBuilder<object, ScanningActivatorData, DynamicRegistrationStyle> registrationBuilder)
        {
            return registrationBuilder
                .Where(t =>
                {
                    var @interface = t.GetInterface($"I{t.Name}");

                    if (@interface != null)
                    {
                        Console.WriteLine($"{t.Name} -  {@interface.Name}");
                    }
                    
                    return @interface != null;
                })
                .AsImplementedInterfaces()
                .SingleInstance()
                .PreserveExistingDefaults();
        }
    }    
}

