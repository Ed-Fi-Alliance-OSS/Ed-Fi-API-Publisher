// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Helpers
{
    /// <summary>
    /// A non-seekable read-only stream that records whether it was disposed.
    /// </summary>
    internal class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public ForwardOnlyStream(byte[] data)
        {
            _inner = new MemoryStream(data);
        }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _inner.Dispose();

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// HttpContent that hands out a non-seekable stream for the streaming path and records whether the
    /// whole-body buffering path (used by ReadAsStringAsync and by HttpClient's ResponseContentRead
    /// completion option) was ever invoked, and whether the content was disposed.
    /// </summary>
    internal class InstrumentedJsonContent : HttpContent
    {
        private readonly byte[] _data;

        public InstrumentedJsonContent(string json)
        {
            _data = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        public bool BufferingAttempted { get; private set; }
        public bool ContentDisposed { get; private set; }
        public int StreamsCreated { get; private set; }
        public ForwardOnlyStream LastStream { get; private set; }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamsCreated++;
            LastStream = new ForwardOnlyStream(_data);

            return Task.FromResult<Stream>(LastStream);
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context)
        {
            BufferingAttempted = true;

            return stream.WriteAsync(_data, 0, _data.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            ContentDisposed = true;

            base.Dispose(disposing);
        }
    }
}
