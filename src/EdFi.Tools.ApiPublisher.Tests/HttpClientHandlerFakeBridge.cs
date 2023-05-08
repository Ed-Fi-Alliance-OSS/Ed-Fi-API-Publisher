// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
