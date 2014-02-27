﻿using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using MonoTouch.Foundation;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModernHttpClient
{
    public class NSUrlSessionHandler : HttpMessageHandler
    {
        readonly NSUrlSession session;

        readonly Dictionary<NSUrlSessionTask, Tuple<HttpResponseMessage, ByteArrayListStream, CancellationToken>> inflightRequests = 
            new Dictionary<NSUrlSessionTask, Tuple<HttpResponseMessage, ByteArrayListStream, CancellationToken>>();

        public NSUrlSessionHandler()
        {
            session = NSUrlSession.FromConfiguration(
                NSUrlSessionConfiguration.DefaultSessionConfiguration, 
                new DataTaskDelegate(this), null);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers as IEnumerable<KeyValuePair<string, IEnumerable<string>>>;
            var ms = new MemoryStream();

            if (request.Content != null) {
                await request.Content.CopyToAsync(ms).ConfigureAwait(false);
                headers = headers.Union(request.Content.Headers).ToArray();
            }

            var rq = new NSMutableUrlRequest() {
                AllowsCellularAccess = true,
                Body = NSData.FromArray(ms.ToArray()),
                CachePolicy = NSUrlRequestCachePolicy.UseProtocolCachePolicy,
                Headers = headers.Aggregate(new NSMutableDictionary(), (acc, x) => {
                    acc.Add(new NSString(x.Key), new NSString(x.Value.LastOrDefault()));
                    return acc;
                }),
                HttpMethod = request.Method.ToString().ToUpperInvariant(),
                Url = NSUrl.FromString(request.RequestUri.AbsoluteUri),
            };

            var host = request.RequestUri.GetLeftPart(UriPartial.Authority);

            var op = session.CreateDataTask(rq);

            cancellationToken.ThrowIfCancellationRequested();

            lock (inflightRequests) {
                inflightRequests[op] = Tuple.Create(new HttpResponseMessage(), cancellationToken);
            }

            op.Resume();
        }

        class DataTaskDelegate : NSUrlSessionDataDelegate
        {
            NSUrlSessionHandler This { get; set; }
            public DataTaskDelegate(NSUrlSessionHandler that)
            {
                this.This = that;
            }

            public override void DidReceiveResponse(NSUrlSession session, NSUrlSessionDataTask dataTask, NSUrlResponse response, Action<NSUrlSessionResponseDisposition> completionHandler)
            {
                completionHandler(NSUrlSessionResponseDisposition.Allow);
            }

            public override void DidCompleteWithError (NSUrlSession session, NSUrlSessionTask task, NSError error)
            {
            }

            public override void DidReceiveData (NSUrlSession session, NSUrlSessionDataTask dataTask, NSData data)
            {
            }

            public override void WillCacheResponse (NSUrlSession session, NSUrlSessionDataTask dataTask, NSCachedUrlResponse proposedResponse, Action<NSCachedUrlResponse> completionHandler)
            {
                completionHandler(null);
            }

            Tuple<HttpResponseMessage, CancellationToken> getResponseForTask(NSUrlSessionTask task)
            {
                lock (This.inflightRequests) {
                    return This.inflightRequests[task];
                }
            }
        }

        class ByteArrayListStream : Stream
        {
            IDisposable lockRelease = EmptyDisposable.Instance;
            readonly AsyncLock readStreamLock = new AsyncLock();
            readonly List<byte[]> bytes = new List<byte[]>();

            bool isCompleted;
            long maxLength = 0;
            long position = 0;

            public ByteArrayListStream()
            {
                // Initially we have nothing to read so Reads should be parked
                readStreamLock.LockAsync().ContinueWith(t => lockRelease = t.Result);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override void WriteByte(byte value) { throw new NotSupportedException(); }
            public override bool CanSeek { get { return true; } }
            public override bool CanTimeout { get { return false; } }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin)
            { 
                var result = default(long);
                switch (origin) {
                case SeekOrigin.Begin:
                    result = offset;
                    break;
                case SeekOrigin.Current:
                    result = position + offset;
                    break;
                case SeekOrigin.End:
                    result = maxLength + offset;
                    break;
                }
                    
                return (Position = result);
            }

            public override long Position {
                get { return position; }
                set {
                    if (value < 0 || value > maxLength) {
                        throw new ArgumentException();
                    }

                    // Seeking during a read? No way.
                    lock (bytes) {
                        position = value;
                    }
                }
            }

            public override long Length {
                get {
                    return maxLength;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return this.ReadAsync(buffer, offset, count).Result;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                int bytesRead = 0;

                using (await readStreamLock.LockAsync()) {
                    lock (bytes) {
                        int absPositionOfCurrentBuffer = 0;
                        int destOffset = offset;

                        foreach (var buf in bytes) {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Get ourselves to the right buffer
                            if (position > absPositionOfCurrentBuffer) {
                                absPositionOfCurrentBuffer += buf.Length;
                                continue;
                            }

                            int offsetInSrcBuffer = (int)position - absPositionOfCurrentBuffer;
                            int toCopy = Math.Min(count, buf.Length - offsetInSrcBuffer);
                            Array.ConstrainedCopy(buf, offsetInSrcBuffer, buffer, offset, toCopy);

                            bytesRead += toCopy;
                            offset += toCopy;
                            position += toCopy;
                            absPositionOfCurrentBuffer += toCopy;
                            count -= toCopy;

                            if (count < 0) break;
                        }
                    }
                }

                // If we're at the end of the stream and it's not done, prepare
                // the next read to park itself unless AddByteArray or Complete 
                // posts
                if (position >= maxLength && !isCompleted) {
                    readStreamLock.LockAsync().ContinueWith(t => lockRelease = t.Result);
                }

                return bytesRead;
            }

            public void AddByteArray(byte[] arrayToAdd)
            {
                lock (bytes) {
                    maxLength += arrayToAdd.Length;
                    bytes.Add(arrayToAdd);
                }

                Interlocked.Exchange(ref lockRelease, EmptyDisposable.Instance).Dispose();
            }

            public void Complete()
            {
                isCompleted = true;
                Interlocked.Exchange(ref lockRelease, EmptyDisposable.Instance).Dispose();
            }
        }
    }

    sealed class EmptyDisposable : IDisposable
    {
        static readonly IDisposable instance = new EmptyDisposable();
        public static IDisposable Instance { get { return instance; } }

        EmptyDisposable() { }
        public void Dispose() { }
    }
}
