using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenDeepWiki.Agents
{
    /// <summary>
    /// A <see cref="DelegatingHandler"/> that patches the <c>server_tool_usage</c> object in
    /// SSE (text/event-stream) responses so Anthropic-compatible endpoints (e.g. Zhipu
    /// bigmodel) that omit required fields no longer break the official Anthropic SDK.
    ///
    /// The SDK's <c>Anthropic.Models.Messages.ServerToolUsage</c> requires both
    /// <c>web_search_requests</c> and <c>web_fetch_requests</c>; a streaming response whose
    /// usage block only contains one of them makes the SDK throw
    /// <see cref="System.IO.InvalidDataException"/> ("'web_fetch_requests' cannot be absent")
    /// while parsing message_start / message_delta events.
    ///
    /// Missing fields are defaulted to 0; existing values are never modified. The handler is
    /// content-gated: lines without a <c>server_tool_usage</c> object pass through untouched,
    /// so OpenAI-protocol responses (which never contain this field name) are not affected
    /// even though the HttpClient chain is shared across providers.
    /// </summary>
    public sealed class ServerToolUsageNormalizingHandler : DelegatingHandler
    {
        private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<ServerToolUsageNormalizingHandler>();

        // Regex matches: "server_tool_usage"  :  { ... }
        // - the object holds flat scalar counters only, so [^{}] stops at its closing brace
        private static readonly Regex ServerToolUsageRegex =
            new(@"""server_tool_usage""\s*:\s*\{([^{}]*)\}",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // JSON names of the fields required by the SDK's ServerToolUsage model.
        private static readonly string[] RequiredFields = new string[]
        {
            "web_search_requests",
            "web_fetch_requests"
        };

        public ServerToolUsageNormalizingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

            try
            {
                if (IsSseResponse(response))
                {
                    response.Content = WrapSseContent(response.Content);
                }
            }
            catch (Exception ex)
            {
                // Never break the call - fall through with the original response
                Logger.Warning(ex,
                    "ServerToolUsageNormalizingHandler: failed to wrap SSE content; returning original response.");
            }

            return response;
        }

        private static bool IsSseResponse(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var ct = response.Content.Headers.ContentType;
            return ct != null &&
                   ct.MediaType != null &&
                   ct.MediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);
        }

        private static HttpContent WrapSseContent(HttpContent original)
        {
            // Same approach as FinishReasonNormalizingHandler: read the original stream
            // lazily and transform it line-by-line without buffering the whole body.
            var transforming = new TransformingStreamContent(original);
            // Copy all content headers from the original so Content-Type / encoding
            // are preserved downstream.
            foreach (var header in original.Headers)
            {
                transforming.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return transforming;
        }

        /// <summary>
        /// Patches a single SSE data line by appending any required field that is missing
        /// from a <c>server_tool_usage</c> object. Lines without such an object (including
        /// OpenAI-protocol events) are returned unchanged.
        ///
        /// Exposed as <c>internal static</c> for unit tests.
        /// </summary>
        internal static string TransformLine(string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                return line;
            }

            // Quick check before running regex
            if (!line.Contains("\"server_tool_usage\"", StringComparison.Ordinal))
            {
                return line;
            }

            return ServerToolUsageRegex.Replace(line, match =>
            {
                var body = match.Groups[1].Value;
                var patched = body;
                foreach (var field in RequiredFields)
                {
                    if (!patched.Contains("\"" + field + "\"", StringComparison.Ordinal))
                    {
                        patched = AppendField(patched, field);
                    }
                }

                if (string.Equals(patched, body, StringComparison.Ordinal))
                {
                    return match.Value; // nothing missing, avoid allocation
                }

                Logger.Debug(
                    "ServerToolUsageNormalizingHandler: added missing server_tool_usage fields to SSE event.");
                // Reconstruct: keep the "server_tool_usage":{ prefix intact, swap the body.
                var prefixLength = match.Groups[1].Index - match.Index;
                return match.Value.Substring(0, prefixLength) + patched + "}";
            });
        }

        private static string AppendField(string body, string field)
        {
            var insertion = "\"" + field + "\":0";
            if (string.IsNullOrWhiteSpace(body))
            {
                return insertion;
            }

            return body + "," + insertion;
        }

        // --------------------------------------------------------------------- inner types

        /// <summary>
        /// An <see cref="HttpContent"/> implementation that wraps the original SSE content
        /// and transforms it line-by-line on the fly without buffering the entire body.
        /// </summary>
        private sealed class TransformingStreamContent : HttpContent
        {
            private readonly HttpContent _inner;

            public TransformingStreamContent(HttpContent inner)
            {
                _inner = inner;
            }

            protected override async Task SerializeToStreamAsync(
                Stream stream,
                TransportContext? context)
            {
                var innerStream = await _inner.ReadAsStreamAsync();
                await TransformSseStreamAsync(innerStream, stream, CancellationToken.None);
            }

            protected override async Task SerializeToStreamAsync(
                Stream stream,
                TransportContext? context,
                CancellationToken cancellationToken)
            {
                var innerStream = await _inner.ReadAsStreamAsync(cancellationToken);
                await TransformSseStreamAsync(innerStream, stream, cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                // Length unknown until we transform; signal that to the framework
                length = -1;
                return false;
            }

            private static async Task TransformSseStreamAsync(
                Stream source,
                Stream destination,
                CancellationToken cancellationToken)
            {
                var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096, leaveOpen: true);
                // Use a writer that does NOT add a BOM and flushes after each write so the
                // HTTP client can stream the data to the SDK progressively.
                var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    var transformed = TransformLine(line);
                    // ReadLineAsync strips the newline; we must restore it so the
                    // SDK receives properly framed SSE events.
                    await writer.WriteLineAsync(transformed.AsMemory(), cancellationToken);
                }

                await writer.FlushAsync(cancellationToken);
            }
        }
    }
}
