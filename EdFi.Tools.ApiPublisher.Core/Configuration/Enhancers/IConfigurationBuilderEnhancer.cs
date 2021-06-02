using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers
{
    public interface IConfigurationBuilderEnhancer
    {
        void Enhance(IConfigurationBuilder configurationBuilder);
    }
}