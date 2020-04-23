using System;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    public sealed class ApiConnectionsConfigurationSourceNameAttribute : Attribute
    {
        public string Name { get; }

        public ApiConnectionsConfigurationSourceNameAttribute(string name)
        {
            Name = name;
        }
    }
}