using System;
using System.Linq;
using System.Net.Http;

namespace EdFi.Tools.ApiPublisher.Tests.Extensions
{
    public static class HttpRequestMessageExtensions
    {
        public static bool HasParameter(this HttpRequestMessage request, string parameterName)
        {
            return request
                ?.RequestUri
                ?.ParseQueryString()
                ?.GetValues(parameterName)
                ?.SingleOrDefault() != null;
        }

        public static T QueryString<T>(this HttpRequestMessage request, string parameterName)
        {
            var value = request?.RequestUri?.ParseQueryString()?.GetValues(parameterName)?.SingleOrDefault();

            if (value == null)
            {
                return default;
            }
            
            return (T) Convert.ChangeType(value, typeof(T));
        }
    }
}