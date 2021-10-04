using System.Collections;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Models;
using EdFi.Tools.ApiPublisher.Tests.Resources;
using EdFi.Tools.ApiPublisher.Tests.Serialization;
using FakeItEasy;
using FakeItEasy.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace EdFi.Tools.ApiPublisher.Tests
{
    public static class MockRequests
    {
        public const string SourceApiBaseUrl = "https://test.source"; 
        public const string TargetApiBaseUrl = "https://test.target";

        public const string DataManagementPath = "/data/v3";

        public static JsonSerializerSettings SerializerSettings =>
            new JsonSerializerSettings
            {
                ContractResolver =
                    new DefaultContractResolver
                    {
                        NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = true }
                    },
                Converters = new JsonConverter[] { new Iso8601UtcDateOnlyConverter() }
            };
        
        // public static void NotFound(this IFakeHttpClientHandler fakeRequestHandler, string url)
        // {
        //     A.CallTo(() => fakeRequestHandler.Get(url, A<HttpRequestMessage>.Ignored))
        //         .Returns(FakeResponse.NotFound());
        // }

        public static IFakeHttpRequestHandler GetResourceData<TData>(
            this IFakeHttpRequestHandler fakeRequestHandler,
            string url,
            IReadOnlyList<TData> data)
        {
            return GetResourceData(fakeRequestHandler, url, null, data);
        }

        public static IFakeHttpRequestHandler GetResourceData<TData>(
            this IFakeHttpRequestHandler fakeRequestHandler,
            string url,
            IDictionary<string, string> parameters,
            IReadOnlyList<TData> data)
        {
            A.CallTo(
                    () => fakeRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => 
                                (msg.RequestUri.LocalPath == url || Regex.IsMatch(msg.RequestUri.LocalPath, url)) 
                                && !HasTotalCountParameter(msg) 
                                && RequestMatchesParameters(msg, parameters))))
                .Returns(FakeResponse.OK(data));

            return fakeRequestHandler;
        }

        private static bool RequestMatchesParameters(HttpRequestMessage request, IDictionary<string,string> parameters)
        {
            if (parameters == null)
            {
                return true;
            }
            
            var queryString = request.RequestUri.ParseQueryString();

            foreach (var parameter in parameters)
            {
                if (queryString[parameter.Key] != parameter.Value)
                {
                    return false;
                }
            }

            return true;
        }

        public static IFakeHttpRequestHandler PostResource(this IFakeHttpRequestHandler fakeRequestHandler, string url, params HttpStatusCode[] responseCodes)
        {
            var mocker = A.CallTo(
                    () => fakeRequestHandler.Post(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => (msg.RequestUri.LocalPath == url || Regex.IsMatch(msg.RequestUri.LocalPath, url)))))
                .Returns(CreateMessageWithAppropriateBody(responseCodes.First()))
                .Once();

            foreach (var httpStatusCode in responseCodes.Skip(1))
            {
                mocker.Then.Returns(CreateMessageWithAppropriateBody(httpStatusCode));
            }

            return fakeRequestHandler;

            HttpResponseMessage CreateMessageWithAppropriateBody(HttpStatusCode statusCode)
            {
                // Return a stock body in the response for a 400 or 500 series status code
                if ((int)statusCode >= 400)
                {
                    return new HttpResponseMessage(statusCode)
                    {
                        Content = new StringContent(
                            "{ 'message': 'A test error occurred.' }",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(statusCode);
            }
        }

        public static IFakeHttpRequestHandler ResourceCount(
            this IFakeHttpRequestHandler fakeRequestHandler,
            string resourcePath = null,
            int responseTotalCountHeader = 10)
        {
            IReturnValueArgumentValidationConfiguration<HttpResponseMessage> fakeCall;

            if (!string.IsNullOrEmpty(resourcePath))
            {
                fakeCall = A.CallTo(() => fakeRequestHandler.Get(
                    $"{fakeRequestHandler.BaseUrl}{DataManagementPath}{resourcePath}",
                    A<HttpRequestMessage>.That.Matches(msg => HasTotalCountParameter(msg))));
            }
            else
            {
                fakeCall = A.CallTo(() => fakeRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => HasTotalCountParameter(msg))));
            }
            
            fakeCall.Returns(FakeResponse.OK("[]").AppendHeaders(("Total-Count", responseTotalCountHeader.ToString())));

            return fakeRequestHandler;
        }

        private static bool HasTotalCountParameter(HttpRequestMessage request)
        {
            var queryString = request.RequestUri.ParseQueryString();

            return queryString["totalCount"] == "true";
        }

        
        public static IFakeHttpRequestHandler Dependencies(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(
                    () => fakeRequestHandler.Get(
                        $"{fakeRequestHandler.BaseUrl}/metadata/data/v3/dependencies",
                        A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK(TestData.Dependencies.GraphML()));
            
            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler ApiVersionMetadata(
            this IFakeHttpRequestHandler fakeRequestHandler,
            string apiVersion = "5.2",
            string edfiVersion = "3.3.0-a")
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(
                    FakeResponse.OK(
                        new
                        {
                            version = apiVersion,
                            dataModels = new[]
                            {
                                new
                                {
                                    name = "Ed-Fi",
                                    version = edfiVersion
                                }
                            }
                        }));

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler OAuthToken(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Post($"{fakeRequestHandler.BaseUrl}/oauth/token", A<HttpRequestMessage>.Ignored))
                .Returns(
                    FakeResponse.OK(
                        new
                        {
                            access_token = "TheAccessToken",
                            // scope = "255901"
                        }));
            
            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler Snapshots(this IFakeHttpRequestHandler fakeRequestHandler, object[] data)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/changeQueries/v1/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK(JsonConvert.SerializeObject(data)));
            
            return fakeRequestHandler;
        }
        
        public static IFakeHttpRequestHandler SnapshotsEmpty(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/changeQueries/v1/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK("[]"));
            
            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SnapshotsNotFound(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/changeQueries/v1/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.NotFound());
            
            return fakeRequestHandler;
        }
        
        public static IFakeHttpRequestHandler LegacySnapshotsNotFound(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/data/v3/publishing/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.NotFound());
            
            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler AvailableChangeVersions(this IFakeHttpRequestHandler fakeRequestHandler, int newestAvailableChangeVersion)
        {
            A.CallTo(
                    () => fakeRequestHandler.Get(
                        $"{fakeRequestHandler.BaseUrl}/changeQueries/v1/availableChangeVersions",
                        A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK(new { newestChangeVersion = newestAvailableChangeVersion }));

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SetBaseUrl(this IFakeHttpRequestHandler fakeRequestHandler, string apiBaseUrl)
        {
            A.CallTo(() => fakeRequestHandler.BaseUrl).Returns(apiBaseUrl);
            
            return fakeRequestHandler;
        }
    }
}