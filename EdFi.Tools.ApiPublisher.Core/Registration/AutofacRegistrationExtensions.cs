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

