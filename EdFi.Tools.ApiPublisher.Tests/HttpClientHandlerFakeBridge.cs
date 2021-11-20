using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests
{
    public class HttpClientHandlerFakeBridge : HttpClientHandler
    {
        private readonly IFakeHttpRequestHandler _handler;

        public HttpClientHandlerFakeBridge(IFakeHttpRequestHandler handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Requested URL: {request.Method} {request.RequestUri}");

            string requestPath = $"{request.RequestUri.Scheme}://{request.RequestUri.Host}{request.RequestUri.LocalPath}";
            
            switch (request.Method.ToString().ToUpper())
            {
                case "GET":
                    return Task.FromResult(_handler.Get(requestPath, request));
                case "PUT":
                    return Task.FromResult(_handler.Put(requestPath, request));
                case "POST":
                    return Task.FromResult(_handler.Post(requestPath, request));
                case "DELETE":
                    return Task.FromResult(_handler.Delete(requestPath, request));
                default:
                    throw new NotSupportedException($"Mocking of requests of type '{request.Method}' have not been implemented.");
            }
        }
    }
}