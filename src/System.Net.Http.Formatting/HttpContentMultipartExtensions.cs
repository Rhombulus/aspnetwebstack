﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net.Http.Formatting.Parsers;
using System.Threading.Tasks;
using System.Web.Http;

namespace System.Net.Http
{
    /// <summary>
    /// Extension methods to read MIME multipart entities from <see cref="HttpContent"/> instances.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class HttpContentMultipartExtensions
    {
        private const int MinBufferSize = 256;
        private const int DefaultBufferSize = 32 * 1024;

        /// <summary>
        /// Determines whether the specified content is MIME multipart content.
        /// </summary>
        /// <param name="content">The content.</param>
        /// <returns>
        ///   <c>true</c> if the specified content is MIME multipart content; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsMimeMultipartContent(this HttpContent content)
        {
            if (content == null)
            {
                throw Error.ArgumentNull("content");
            }

            return MimeMultipartBodyPartParser.IsMimeMultipartContent(content);
        }

        /// <summary>
        /// Determines whether the specified content is MIME multipart content with the 
        /// specified subtype. For example, the subtype <c>mixed</c> would match content
        /// with a content type of <c>multipart/mixed</c>. 
        /// </summary>
        /// <param name="content">The content.</param>
        /// <param name="subtype">The MIME multipart subtype to match.</param>
        /// <returns>
        ///   <c>true</c> if the specified content is MIME multipart content with the specified subtype; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsMimeMultipartContent(this HttpContent content, string subtype)
        {
            if (String.IsNullOrWhiteSpace(subtype))
            {
                throw Error.ArgumentNull("subtype");
            }

            if (IsMimeMultipartContent(content))
            {
                if (content.Headers.ContentType.MediaType.Equals("multipart/" + subtype, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads all body parts within a MIME multipart message into memory using a <see cref="MultipartMemoryStreamProvider"/>.
        /// </summary>
        /// <param name="content">An existing <see cref="HttpContent"/> instance to use for the object's content.</param>
        /// <returns>A <see cref="Task{T}"/> representing the tasks of getting the result of reading the MIME content.</returns>
        public static Task<MultipartMemoryStreamProvider> ReadAsMultipartAsync(this HttpContent content)
        {
            return ReadAsMultipartAsync<MultipartMemoryStreamProvider>(content, new MultipartMemoryStreamProvider(), DefaultBufferSize);
        }

        /// <summary>
        /// Reads all body parts within a MIME multipart message using the provided <see cref="MultipartStreamProvider"/> instance
        /// to determine where the contents of each body part is written. 
        /// </summary>
        /// <typeparam name="T">The <see cref="MultipartStreamProvider"/> with which to process the data.</typeparam>
        /// <param name="content">An existing <see cref="HttpContent"/> instance to use for the object's content.</param>
        /// <param name="streamProvider">A stream provider providing output streams for where to write body parts as they are parsed.</param>
        /// <returns>A <see cref="Task{T}"/> representing the tasks of getting the result of reading the MIME content.</returns>
        public static Task<T> ReadAsMultipartAsync<T>(this HttpContent content, T streamProvider) where T : MultipartStreamProvider
        {
            return ReadAsMultipartAsync(content, streamProvider, DefaultBufferSize);
        }

        /// <summary>
        /// Reads all body parts within a MIME multipart message using the provided <see cref="MultipartStreamProvider"/> instance
        /// to determine where the contents of each body part is written and <paramref name="bufferSize"/> as read buffer size.
        /// </summary>
        /// <typeparam name="T">The <see cref="MultipartStreamProvider"/> with which to process the data.</typeparam>
        /// <param name="content">An existing <see cref="HttpContent"/> instance to use for the object's content.</param>
        /// <param name="streamProvider">A stream provider providing output streams for where to write body parts as they are parsed.</param>
        /// <param name="bufferSize">Size of the buffer used to read the contents.</param>
        /// <returns>A <see cref="Task{T}"/> representing the tasks of getting the result of reading the MIME content.</returns>
        public static async Task<T> ReadAsMultipartAsync<T>(this HttpContent content, T streamProvider, int bufferSize) where T : MultipartStreamProvider
        {
            if (content == null)
            {
                throw Error.ArgumentNull("content");
            }

            if (streamProvider == null)
            {
                throw Error.ArgumentNull("streamProvider");
            }

            if (bufferSize < MinBufferSize)
            {
                throw Error.ArgumentMustBeGreaterThanOrEqualTo("bufferSize", bufferSize, MinBufferSize);
            }

            Stream stream = null;
            try
            {
                try
                {
                    stream = await content.ReadAsStreamAsync();
                }
                catch (Exception e)
                {
                    throw new IOException(Properties.Resources.ReadAsMimeMultipartErrorReading, e);
                }

                using (var parser = new MimeMultipartBodyPartParser(content, streamProvider))
                {
                    byte[] data = new byte[bufferSize];
                    MultipartAsyncContext context = new MultipartAsyncContext(content, stream, parser, data, streamProvider.Contents);

                    // Start async read/write loop
                    await MultipartReadAsync(context);

                    // Let the stream provider post-process when everything is complete
                    await streamProvider.ExecutePostProcessingAsync();
                    return streamProvider;
                }
            }
            finally
            {
                if (stream != null)
                {
                    stream.Dispose();
                }
            }
        }

        private static async Task MultipartReadAsync(MultipartAsyncContext context)
        {
            Contract.Assert(context != null, "context cannot be null");
            int bytesRead = 0;
            try
            {
                bytesRead = await context.ContentStream.ReadAsync(context.Data, 0, context.Data.Length);
            }
            catch (Exception e)
            {
                throw new IOException(Properties.Resources.ReadAsMimeMultipartErrorReading, e);
            }

            IEnumerable<MimeBodyPart> parts = context.MimeParser.ParseBuffer(context.Data, bytesRead);
            context.PartsEnumerator = parts.GetEnumerator();
            await MoveToNextPartAsync(context);
        }

        private static async Task MoveToNextPartAsync(MultipartAsyncContext context)
        {
            Contract.Assert(context != null, "context cannot be null");
            while (context.PartsEnumerator.MoveNext())
            {
                context.SegmentsEnumerator = context.PartsEnumerator.Current.Segments.GetEnumerator();
                if (await MoveToNextSegmentAsync(context))
                {
                    return;
                }
            }

            await MultipartReadAsync(context);
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Exception is propagated.")]
        private static async Task<bool> MoveToNextSegmentAsync(MultipartAsyncContext context)
        {
            Contract.Assert(context != null, "context cannot be null");
            if (context.SegmentsEnumerator.MoveNext())
            {
                Stream output = context.PartsEnumerator.Current.GetOutputStream(context.Parent);
                try
                {
                    ArraySegment<byte> segment = (ArraySegment<byte>)context.SegmentsEnumerator.Current;
                    await output.WriteAsync(segment.Array, segment.Offset, segment.Count);

                    if (!await MoveToNextSegmentAsync(context))
                    {
                        await MoveToNextPartAsync(context);
                    }

                    return true;
                }
                catch (Exception e)
                {
                    context.PartsEnumerator.Current.Dispose();
                    throw new IOException(Properties.Resources.ReadAsMimeMultipartErrorWriting, e);
                }
            }

            return CheckPartCompletion(context.PartsEnumerator.Current, context.Result);
        }

        private static bool CheckPartCompletion(MimeBodyPart part, ICollection<HttpContent> result)
        {
            Contract.Assert(part != null, "part cannot be null.");
            Contract.Assert(result != null, "result cannot be null.");
            if (part.IsComplete)
            {
                if (part.HttpContent != null)
                {
                    result.Add(part.HttpContent);
                }

                bool isFinal = part.IsFinal;
                part.Dispose();
                return isFinal;
            }

            return false;
        }

        /// <summary>
        /// Managing state for asynchronous read and write operations
        /// </summary>
        private class MultipartAsyncContext
        {
            public MultipartAsyncContext(HttpContent parent, Stream contentStream,  MimeMultipartBodyPartParser mimeParser, byte[] data, ICollection<HttpContent> result)
            {
                Contract.Assert(parent != null);
                Contract.Assert(contentStream != null);
                Contract.Assert(mimeParser != null);
                Contract.Assert(data != null);

                Parent = parent;
                ContentStream = contentStream;
                Result = result;
                MimeParser = mimeParser;
                Data = data;
            }

            /// <summary>
            /// Gets the parent HttpContent MIME content.
            /// </summary>
            public HttpContent Parent { get; private set; }

            /// <summary>
            /// Gets the <see cref="Stream"/> that we read from.
            /// </summary>
            public Stream ContentStream { get; private set; }

            /// <summary>
            /// Gets the collection of parsed <see cref="HttpContent"/> instances.
            /// </summary>
            public ICollection<HttpContent> Result { get; private set; }

            /// <summary>
            /// The data buffer that we use for reading data from the input stream into before processing.
            /// </summary>
            public byte[] Data { get; private set; }

            /// <summary>
            /// Gets the MIME parser instance used to parse the data
            /// </summary>
            public MimeMultipartBodyPartParser MimeParser { get; private set; }

            /// <summary>
            /// Gets or sets the parts enumerator for going through the parsed parts.
            /// </summary>
            public IEnumerator<MimeBodyPart> PartsEnumerator { get; set; }

            /// <summary>
            /// Gets or sets the segments enumerator for going through the segments within each part.
            /// </summary>
            public IEnumerator SegmentsEnumerator { get; set; }
        }
    }
}
