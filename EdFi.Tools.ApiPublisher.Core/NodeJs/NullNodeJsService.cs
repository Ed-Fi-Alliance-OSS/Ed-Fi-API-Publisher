using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jering.Javascript.NodeJS;

namespace EdFi.Tools.ApiPublisher.Core.NodeJs;

public class NullNodeJsService : INodeJSService
{
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public Task<T?> InvokeFromFileAsync<T>(
        string modulePath,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromFileAsync(
        string modulePath,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T?> InvokeFromStringAsync<T>(
        string moduleString,
        string? cacheIdentifier = null,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStringAsync(
        string moduleString,
        string? cacheIdentifier = null,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T?> InvokeFromStringAsync<T>(
        Func<string> moduleFactory,
        string cacheIdentifier,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStringAsync(
        Func<string> moduleFactory,
        string cacheIdentifier,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T?> InvokeFromStreamAsync<T>(
        Stream moduleStream,
        string? cacheIdentifier = null,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStreamAsync(
        Stream moduleStream,
        string? cacheIdentifier = null,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<T?> InvokeFromStreamAsync<T>(
        Func<Stream> moduleFactory,
        string cacheIdentifier,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task InvokeFromStreamAsync(
        Func<Stream> moduleFactory,
        string cacheIdentifier,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<(bool, T?)> TryInvokeFromCacheAsync<T>(
        string cacheIdentifier,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<bool> TryInvokeFromCacheAsync(
        string cacheIdentifier,
        string? exportName = null,
        object?[]? args = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public void MoveToNewProcess()
    {
        throw new NotImplementedException();
    }
}