// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// Reads the body of a <c>GET /{resource}/partitions</c> response into a string under a size cap. The documented
/// body is a short JSON object holding at most a few hundred page tokens, so it is buffered whole; the cap keeps
/// a misbehaving source from making the publisher buffer an arbitrarily large body (see APIPUB-139 review).
/// </summary>
public static class PartitionsResponseBody
{
    /// <summary>
    /// The largest partitions body accepted, in bytes. The 200-token maximum the API allows is a few kilobytes,
    /// so this is generous by two orders of magnitude while still bounding memory.
    /// </summary>
    public const int MaxBytes = 1_048_576;

    private const int ChunkSize = 16 * 1024;

    /// <summary>
    /// Reads the response body as UTF-8 text (JSON is UTF-8 per RFC 8259, and the Ed-Fi ODS API always emits it),
    /// honoring <paramref name="cancellationToken" /> during the read.
    /// </summary>
    /// <exception cref="InvalidDataException">The body exceeded <see cref="MaxBytes" />.</exception>
    public static async Task<string> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return string.Empty;
        }

        await using var responseStream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();

        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            int bytesRead;

            while ((bytesRead = await responseStream.ReadAsync(chunk.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + bytesRead > MaxBytes)
                {
                    throw new InvalidDataException(
                        $"The partitions response body exceeded the {MaxBytes:N0}-byte limit the publisher accepts for a list of page tokens.");
                }

                await body.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }
}
