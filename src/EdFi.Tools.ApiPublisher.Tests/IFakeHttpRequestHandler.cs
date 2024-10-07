// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http;

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
