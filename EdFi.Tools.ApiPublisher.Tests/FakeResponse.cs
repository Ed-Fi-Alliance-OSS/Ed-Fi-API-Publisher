using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace EdFi.Tools.ApiPublisher.Tests
{
    public static class FakeResponse
    {
        public static HttpResponseMessage OK(string content) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };

        public static HttpResponseMessage OK(object data) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(data, MockRequests.SerializerSettings), Encoding.UTF8, "application/json")
            };

        public static HttpResponseMessage NotFound() => new HttpResponseMessage(HttpStatusCode.NotFound);

        // public static HttpResponseMessage StatusCodeResult(HttpStatusCode responseCode)
        // {
        //     return new HttpResponseMessage(responseCode);
        // }
    }

    public static class HttpResponseMessageExtensions
    {
        public static HttpResponseMessage AppendHeaders(this HttpResponseMessage message, params (string, string)[] headers)
        {
            foreach (var tuple in headers)
            {
                message.Headers.Add(tuple.Item1, tuple.Item2);
            }

            return message;
        }
    }
}