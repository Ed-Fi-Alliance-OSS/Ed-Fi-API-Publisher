// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Models;
using EdFi.Tools.ApiPublisher.Tests.Resources;
using EdFi.Tools.ApiPublisher.Tests.Serialization;
using FakeItEasy;
using FakeItEasy.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace EdFi.Tools.ApiPublisher.Tests
{
    public static class MockRequests
    {
        public const string SourceApiBaseUrl = "https://test.source";
        public const string SourceAuthenticateServiceUrl = "https://test.source.auth.provider.url";

        public const string TargetApiBaseUrl = "https://test.target";

        public const string DataManagementPath = "/data/v3";

        public const string OdsApiToken = "TheAccessToken";
        public const string AuthServiceToken = "AuthServiceAccessToken";



        public static readonly string SchoolYearSpecificDataManagementPath = $"/data/v3/{SchoolYear}";
        public const int SchoolYear = 2099;

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

        public static IFakeHttpRequestHandler GetResourceDataItem<TData>(
            this IFakeHttpRequestHandler fakeRequestHandler,
            string url,
            TData data)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/{url}",
                A<HttpRequestMessage>.Ignored))
               .Returns(FakeResponse.OK(JsonConvert.SerializeObject(data)));

            return fakeRequestHandler;
        }

        private static bool RequestMatchesParameters(HttpRequestMessage request, IDictionary<string, string> parameters)
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

        public static IFakeHttpRequestHandler PostResource(
            this IFakeHttpRequestHandler fakeRequestHandler,
            string url,
            params HttpStatusCode[] responseCodes)
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

        public static IFakeHttpRequestHandler PostResource(this IFakeHttpRequestHandler fakeRequestHandler, string url, params (HttpStatusCode, JObject)[] responses)
        {
            var mocker = A.CallTo(
                    () => fakeRequestHandler.Post(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => (msg.RequestUri.LocalPath == url || Regex.IsMatch(msg.RequestUri.LocalPath, url)))))
                .Returns(CreateMessageWithAppropriateBody(responses.First()))
                .Once();

            foreach (var httpStatusCode in responses.Skip(1))
            {
                mocker.Then.Returns(CreateMessageWithAppropriateBody(httpStatusCode));
            }

            return fakeRequestHandler;

            HttpResponseMessage CreateMessageWithAppropriateBody((HttpStatusCode, JObject) response)
            {
                var (statusCode, body) = response;

                if (body == null)
                {
                    return new HttpResponseMessage(statusCode);
                }

                // Return a stock body in the response for a 400 or 500 series status code
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(
                        body.ToString(),
                        Encoding.UTF8,
                        "application/json")
                };
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
                    $"{fakeRequestHandler.BaseUrl}{fakeRequestHandler.DataManagementUrlSegment}{resourcePath}",
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
                        $"{fakeRequestHandler.BaseUrl}/metadata/{fakeRequestHandler.DataManagementUrlSegment}/dependencies",
                        A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK(TestData.Dependencies.GraphML()));

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler Dependencies(this IFakeHttpRequestHandler fakeRequestHandler, string resourcePath)
        {
            A.CallTo(
                    () => fakeRequestHandler.Get(
                        $"{fakeRequestHandler.BaseUrl}/metadata/{fakeRequestHandler.DataManagementUrlSegment}/dependencies",
                        A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK($@"<?xml version=""1.0"" encoding=""utf-8""?>
<graphml xmlns=""http://graphml.graphdrawing.org/xmlns"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xsi:schemaLocation=""http://graphml.graphdrawing.org/xmlns http://graphml.graphdrawing.org/xmlns/1.0/graphml.xsd"">
  <graph id=""EdFi Dependencies"" edgedefault=""directed"">
    <node id=""{resourcePath}""/>
  </graph>
</graphml>
"));

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

        public static IFakeHttpRequestHandler ApiVersionMetadataUrls(
            this IFakeHttpRequestHandler fakeRequestHandler,
            string apiVersion,
            string edfiVersion,
            Dictionary<string, string> urls
            )
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
                            },
                            urls = JObject.FromObject(urls)
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
                            access_token = OdsApiToken,
                            // scope = "255901"
                        }));

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SeparateAuthServiceToken(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Post($"{SourceAuthenticateServiceUrl}/", A<HttpRequestMessage>.Ignored))
                .Returns(
                    FakeResponse.OK(
                        new
                        {
                            access_token = AuthServiceToken,
                            // scope = "255901"
                        }));


            return fakeRequestHandler;
        }


        public static IFakeHttpRequestHandler Snapshots(this IFakeHttpRequestHandler fakeRequestHandler, Snapshot[] data)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/{fakeRequestHandler.ChangeQueriesUrlSegment}/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK(JsonConvert.SerializeObject(data)));

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SnapshotsEmpty(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/{fakeRequestHandler.ChangeQueriesUrlSegment}/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK("[]"));

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SnapshotsNotFound(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/{fakeRequestHandler.ChangeQueriesUrlSegment}/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.NotFound());

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler LegacySnapshotsNotFound(this IFakeHttpRequestHandler fakeRequestHandler)
        {
            A.CallTo(() => fakeRequestHandler.Get($"{fakeRequestHandler.BaseUrl}/{fakeRequestHandler.DataManagementUrlSegment}/publishing/snapshots", A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.NotFound());

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler AvailableChangeVersions(this IFakeHttpRequestHandler fakeRequestHandler, int newestAvailableChangeVersion)
        {
            A.CallTo(
                    () => fakeRequestHandler.Get(
                        $"{fakeRequestHandler.BaseUrl}/{fakeRequestHandler.ChangeQueriesUrlSegment}/availableChangeVersions", //  changeQueries/v1 or changeQueries/v1/2099 
                        A<HttpRequestMessage>.Ignored))
                .Returns(FakeResponse.OK(new { newestChangeVersion = newestAvailableChangeVersion }));

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SetBaseUrl(this IFakeHttpRequestHandler fakeRequestHandler, string apiBaseUrl)
        {
            A.CallTo(() => fakeRequestHandler.BaseUrl).Returns(apiBaseUrl);

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SetDataManagementUrlSegment(this IFakeHttpRequestHandler fakeRequestHandler, string urlSegment)
        {
            A.CallTo(() => fakeRequestHandler.DataManagementUrlSegment).Returns(urlSegment);

            return fakeRequestHandler;
        }

        public static IFakeHttpRequestHandler SetChangeQueriesUrlSegment(this IFakeHttpRequestHandler fakeRequestHandler, string urlSegment)
        {
            A.CallTo(() => fakeRequestHandler.ChangeQueriesUrlSegment).Returns(urlSegment);

            return fakeRequestHandler;
        }
    }
}
