using System.Net;

namespace EdFi.Tools.ApiPublisher.Core.Extensions
{
    public static class HttpStatusCodeExtensions
    {
        public static bool IsPermanentFailure(this HttpStatusCode httpStatusCode)
        {
            switch (httpStatusCode)
            {
                case HttpStatusCode.InternalServerError:
                case HttpStatusCode.GatewayTimeout:
                case HttpStatusCode.ServiceUnavailable:
                    return false;
                default:
                    return true;
            }
        }
    }
}