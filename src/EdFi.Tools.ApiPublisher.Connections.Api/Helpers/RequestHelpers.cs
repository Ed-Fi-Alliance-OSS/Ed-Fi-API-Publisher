// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Common.Inflection;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Helpers;

public static class RequestHelpers
{
    private static readonly char[] _pathSeparatorChars = { '/' };
    private static readonly ConcurrentDictionary<string, string> _writableContentTypeByResourceUrl = new();
    private static readonly ConcurrentDictionary<string, string> _readableContentTypeByResourceUrl = new();

    /// <summary>
    /// Sends a POST request (applying a Profile content type via the Content-Type header, if appropriate).
    /// </summary>
    /// <param name="edFiApiClient"></param>
    /// <param name="resourceUrl"></param>
    /// <param name="requestUri"></param>
    /// <param name="requestBodyJson"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public static async Task<HttpResponseMessage> SendPostRequestAsync(
        EdFiApiClient edFiApiClient,
        string resourceUrl,
        string requestUri,
        string requestBodyJson,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(edFiApiClient.ConnectionDetails.ProfileName) && ShouldApplyProfileContentType(edFiApiClient.HttpClient.BaseAddress + requestUri))
        {
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri);

            string contentType = _writableContentTypeByResourceUrl.GetOrAdd(
                resourceUrl,
                url =>
                {
                    var segment = new StringSegment(url);
                    var tokenizer = segment.Split(_pathSeparatorChars);
                    string resourceCollectionName = tokenizer.Last().Value;
                    string resourceName = CompositeTermInflector.MakeSingular(resourceCollectionName);

                    return $"application/vnd.ed-fi.{resourceName}.{edFiApiClient.ConnectionDetails.ProfileName.ToLower()}.writable+json";
                });

            request.Content = new StringContent(requestBodyJson, Encoding.UTF8, contentType);

            return await edFiApiClient.HttpClient.SendAsync(request, ct);
        }

        return await edFiApiClient.HttpClient.PostAsync(
            requestUri,
            new StringContent(requestBodyJson, Encoding.UTF8, "application/json"),
            ct);
    }

    /// <summary>
    /// Sends a GET request (applying an applied Profile content type via the Accept header, if appropriate).
    /// </summary>
    /// <param name="edFiApiClient"></param>
    /// <param name="resourceUrl"></param>
    /// <param name="requestUri"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public static async Task<HttpResponseMessage> SendGetRequestAsync(
        EdFiApiClient edFiApiClient,
        string resourceUrl,
        string requestUri,
        CancellationToken ct)
    {
        // Build an explicit request with custom content type
        if (!string.IsNullOrEmpty(edFiApiClient.ConnectionDetails.ProfileName) && ShouldApplyProfileContentType(edFiApiClient.HttpClient.BaseAddress + requestUri))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

            string contentType = _readableContentTypeByResourceUrl.GetOrAdd(
                resourceUrl,
                url =>
                {
                    var segment = new StringSegment(url);
                    var tokenizer = segment.Split(_pathSeparatorChars);
                    string resourceCollectionName = tokenizer.Last().Value;
                    string resourceName = CompositeTermInflector.MakeSingular(resourceCollectionName);

                    return $"application/vnd.ed-fi.{resourceName}.{edFiApiClient.ConnectionDetails.ProfileName.ToLower()}.readable+json";
                });

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(contentType));
            return await edFiApiClient.HttpClient.SendAsync(request, ct);
        }

        return await edFiApiClient.HttpClient.GetAsync(requestUri, ct);
    }

    private static bool ShouldApplyProfileContentType(string requestUri)
    {
        var uri = new Uri(requestUri);

        // Don't apply Profiles to deletes requests
        if (uri.LocalPath.EndsWith("/deletes"))
        {
            return false;
        }

        // Don't apply Profiles to keyChanges requests
        if (uri.LocalPath.EndsWith("/keyChanges"))
        {
            return false;
        }

        // Don't apply Profiles to descriptors requests
        if (uri.LocalPath.EndsWith("Descriptors"))
        {
            return false;
        }

        return true;
    }
}
