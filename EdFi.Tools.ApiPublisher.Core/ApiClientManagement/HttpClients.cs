using System.Net.Http;

namespace EdFi.Tools.ApiPublisher.Core.ApiClientManagement
{
    public class HttpClients
    {
        public HttpClient Source { get; set; }
        public HttpClient Target { get; set; }
    }
}