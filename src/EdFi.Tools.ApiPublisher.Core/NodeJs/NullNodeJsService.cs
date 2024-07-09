// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Jering.Javascript.NodeJS;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.NodeJs;

public class NullNodeJsService : INodeJSService
{
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public Task<T> InvokeFromFileAsync<T>(
        string modulePath,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromFileAsync(
        string modulePath,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T> InvokeFromStringAsync<T>(
        string moduleString,
        string cacheIdentifier = null,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStringAsync(
        string moduleString,
        string cacheIdentifier = null,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T> InvokeFromStringAsync<T>(
        Func<string> moduleFactory,
        string cacheIdentifier,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStringAsync(
        Func<string> moduleFactory,
        string cacheIdentifier,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T> InvokeFromStreamAsync<T>(
        Stream moduleStream,
        string cacheIdentifier = null,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStreamAsync(
        Stream moduleStream,
        string cacheIdentifier = null,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T> InvokeFromStreamAsync<T>(
        Func<Stream> moduleFactory,
        string cacheIdentifier,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStreamAsync(
        Func<Stream> moduleFactory,
        string cacheIdentifier,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<(bool, T)> TryInvokeFromCacheAsync<T>(
        string cacheIdentifier,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<bool> TryInvokeFromCacheAsync(
        string cacheIdentifier,
        string exportName = null,
        object[] args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public void MoveToNewProcess()
    {
        throw new NotImplementedException();
    }

    ValueTask INodeJSService.MoveToNewProcessAsync()
    {
        throw new NotImplementedException();
    }
}
