using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using EdFi.Tools.ApiPublisher.Tests.Models;

namespace EdFi.Tools.ApiPublisher.Tests
{
    public interface IFakeHttpRequestHandler
    {
        string BaseUrl { get; }
        string DataManagementUrlSegment { get; }
        string ChangeQueriesUrlSegment { get; }
        HttpResponseMessage Get(string url, HttpRequestMessage request);
        HttpResponseMessage Post(string url, HttpRequestMessage request);
        HttpResponseMessage Put(string url, HttpRequestMessage request);
        HttpResponseMessage Delete(string url, HttpRequestMessage request);
    }
}