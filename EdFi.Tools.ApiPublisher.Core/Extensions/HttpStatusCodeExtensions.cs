using System.Net;

namespace EdFi.Tools.ApiPublisher.Core.Extensions
{
    public static class HttpStatusCodeExtensions
    {
        public static bool IsPotentiallyTransientFailure(this HttpStatusCode httpStatusCode)
        {
            switch (httpStatusCode)
            {
                case HttpStatusCode.InternalServerError:
                case HttpStatusCode.GatewayTimeout:
                case HttpStatusCode.ServiceUnavailable:
                    return true;
                default:
                    return false;
            }
        }
    }
}