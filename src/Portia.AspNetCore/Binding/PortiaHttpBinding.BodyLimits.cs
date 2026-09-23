using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

public static partial class PortiaHttpBinding
{
    static readonly object BodyLimitAppliedKey = new();

    /// <summary>Applies Portia's body limit to a declared custom request body.</summary>
    /// <param name="context">The current HTTP request.</param>
    public static void EnsureBodyWithinLimit(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var maximum = MaximumBodyBytes(context);
        if (context.Request.ContentLength > maximum)
            throw new HttpPayloadTooLargeException();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature &&
            (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > maximum))
            feature.MaxRequestBodySize = maximum;
        if (context.Items.ContainsKey(BodyLimitAppliedKey))
            return;
        context.Request.Body = new LimitedRequestBodyStream(context.Request.Body, maximum);
        context.Items.Add(BodyLimitAppliedKey, null);
    }

    // The server (Kestrel's MaxRequestBodySize) can reject an oversized body before Portia counts it;
    // that is the same limit breach, so it surfaces as Portia's 413 rather than a malformed request.
    static bool IsServerBodyLimit(BadHttpRequestException exception) =>
        exception is not HttpPayloadTooLargeException &&
        exception.StatusCode == StatusCodes.Status413PayloadTooLarge;

    static bool HasBodyLimitCause(Exception exception)
    {
        for (var cause = exception.InnerException; cause is not null; cause = cause.InnerException)
        {
            if (cause is HttpPayloadTooLargeException ||
                cause is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
                return true;
        }

        return false;
    }

    static long MaximumBodyBytes(HttpContext context)
    {
        var maximum = context.RequestServices.GetService<IOptions<PortiaHttpOptions>>()?.Value.MaxJsonBodyBytes
                      ?? PortiaHttpOptions.DefaultMaxJsonBodyBytes;
        return maximum > 0
            ? maximum
            : throw new InvalidOperationException($"{nameof(PortiaHttpOptions.MaxJsonBodyBytes)} must be positive.");
    }

    sealed class LimitedRequestBodyStream(Stream inner, long maximum) : Stream
    {
        long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
                _ = Count(1);
            return value;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();


        int Count(int read)
        {
            _read += read;
            return _read <= maximum ? read : throw new HttpPayloadTooLargeException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
