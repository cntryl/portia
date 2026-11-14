using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Cntryl.Portia.Tests.Storage;

/// <summary>
/// An in-memory stand-in for the Cloud Storage JSON API used to drive the real Google client without
/// credentials or network access. It implements only the calls the GCP adapter makes.
/// </summary>
public sealed class FakeGcsHandler : HttpMessageHandler
{
    const string KeyVersion = "/cryptoKeyVersions/1";
    readonly object _gate = new();
    long _generation = 1000;
    readonly Dictionary<string, PendingUpload> _uploads = [];
    sealed record PendingUpload(string Bucket, string Name, Dictionary<string, string> Query, Dictionary<string, string>? Metadata, MemoryStream Data);

    /// <summary>A stored object revision.</summary>
    public sealed record Stored(byte[] Data, long Generation, string? KmsKey, Dictionary<string, string>? Metadata);

    /// <summary>A request the adapter sent.</summary>
    public sealed record Recorded(string Method, string Path, IReadOnlyDictionary<string, string> Query, string Body);

    /// <summary>Gets the default Cloud KMS key of each bucket that exists.</summary>
    public Dictionary<string, string> BucketKeys { get; } = [with(StringComparer.Ordinal)];

    /// <summary>Gets the stored objects by bucket and name.</summary>
    public Dictionary<(string Bucket, string Name), Stored> Objects { get; } = [];

    /// <summary>Gets the requests received so far.</summary>
    public List<Recorded> Requests { get; } = [];

    /// <summary>Gets or sets a hook run before the final chunk of each upload is committed, for simulating a concurrent writer.</summary>
    public Action<FakeGcsHandler>? BeforeRewrite { get; set; }

    /// <summary>Gets or sets the key reported on objects written through signed URLs. Null uses the bucket default.</summary>
    public string? SignedUploadKeyOverride { get; set; }

    /// <summary>Stores bytes the way a client PUT to a signed URL would, and returns the generation receipt.</summary>
    public string PutSigned(Uri signedUri, byte[] data)
    {
        var path = Uri.UnescapeDataString(signedUri.AbsolutePath.TrimStart('/'));
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var bucket = path[..slash];
        var name = path[(slash + 1)..];
        lock (_gate)
        {
            var key = SignedUploadKeyOverride ?? BucketKeys[bucket] + KeyVersion;
            var stored = new Stored(data, ++_generation, key, null);
            Objects[(bucket, name)] = stored;
            return stored.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Gets the objects in a bucket whose names start with the prefix.</summary>
    public string[] Names(string bucket, string prefix) =>
        [.. Objects.Keys.Where(key => key.Bucket == bucket && key.Name.StartsWith(prefix, StringComparison.Ordinal)).Select(static key => key.Name).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        var query = HttpUtility.ParseQueryString(uri.Query).AllKeys
            .Where(static key => key is not null)
            .ToDictionary(static key => key!, key => HttpUtility.ParseQueryString(uri.Query)[key]!, StringComparer.Ordinal);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        lock (_gate)
        {
            Requests.Add(new Recorded(request.Method.Method, string.Join('/', segments), query, body));
            return segments[0] == "upload" ? StartUpload(request, segments, query, body) : Route(request, segments, query, body);
        }
    }

    HttpResponseMessage Route(HttpRequestMessage request, string[] segments, Dictionary<string, string> query, string body)
    {
        // storage/v1/b/{bucket}[/o[/{name}[/compose | /rewriteTo/b/{bucket}/o/{name}]]]
        var bucket = segments[3];
        if (segments.Length == 4)
        {
            return BucketKeys.TryGetValue(bucket, out var defaultKey)
                ? Json(HttpStatusCode.OK, new Dictionary<string, object?>
                {
                    ["name"] = bucket,
                    ["encryption"] = new Dictionary<string, object?> { ["defaultKmsKeyName"] = defaultKey }
                })
                : Error(HttpStatusCode.NotFound, "bucket");
        }

        if (segments.Length == 5)
        {
            var prefix = query.GetValueOrDefault("prefix", string.Empty);
            var items = Names(bucket, prefix).Select(name => new Dictionary<string, object?> { ["name"] = name, ["bucket"] = bucket }).ToArray();
            return Json(HttpStatusCode.OK, items.Length == 0 ? [] : new Dictionary<string, object?> { ["items"] = items });
        }

        var name = segments[5];

        if (request.Method == HttpMethod.Delete)
        {
            return Objects.Remove((bucket, name)) ? new HttpResponseMessage(HttpStatusCode.NoContent) : Error(HttpStatusCode.NotFound, "object");
        }

        if (!Objects.TryGetValue((bucket, name), out var stored) ||
            (query.TryGetValue("generation", out var wanted) && wanted != stored.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            return Error(HttpStatusCode.NotFound, "object");
        }

        return query.GetValueOrDefault("alt") == "media" ? Media(request, stored) : Json(HttpStatusCode.OK, Describe(bucket, name, stored));
    }

    HttpResponseMessage StartUpload(HttpRequestMessage request, string[] segments, Dictionary<string, string> query, string body)
    {
        // upload/storage/v1/b/{bucket}/o?uploadType=resumable (start) or ?upload_id=N (chunk)
        var bucket = segments[4];
        if (request.Method == HttpMethod.Post)
        {
            var name = query.GetValueOrDefault("name", string.Empty);
            if (string.IsNullOrWhiteSpace(body) is false)
            {
                using var document = JsonDocument.Parse(body);
                name = document.RootElement.TryGetProperty("name", out var named) ? named.GetString()! : name;
            }

            Dictionary<string, string>? metadata = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("metadata", out var custom))
                {
                    metadata = custom.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.GetString()!, StringComparer.Ordinal);
                }
            }

            var id = (++_generation).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _uploads[id] = new PendingUpload(bucket, name, query, metadata, new MemoryStream());
            var started = new HttpResponseMessage(HttpStatusCode.OK);
            started.Headers.Location = new Uri($"https://storage.googleapis.com/upload/storage/v1/b/{bucket}/o?uploadType=resumable&upload_id={id}");
            return started;
        }

        var pending = _uploads[query["upload_id"]];
        var range = request.Content!.Headers.ContentRange;
        var bytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        pending.Data.Write(bytes);
        if (range?.Length is not { } total || pending.Data.Length < total)
        {
            var more = new HttpResponseMessage((HttpStatusCode)308);
            if (pending.Data.Length > 0)
            {
                more.Headers.TryAddWithoutValidation("Range", $"bytes=0-{pending.Data.Length - 1}");
            }

            return more;
        }

        BeforeRewrite?.Invoke(this);
        if (pending.Query.GetValueOrDefault("ifGenerationMatch") == "0" && Objects.ContainsKey((pending.Bucket, pending.Name)))
        {
            return Error(HttpStatusCode.PreconditionFailed, "destination exists");
        }

        var key = pending.Query.TryGetValue("kmsKeyName", out var requested) ? requested + KeyVersion : null;
        var stored = new Stored(pending.Data.ToArray(), ++_generation, key, pending.Metadata);
        Objects[(pending.Bucket, pending.Name)] = stored;
        _uploads.Remove(query["upload_id"]);
        return Json(HttpStatusCode.OK, Describe(pending.Bucket, pending.Name, stored));
    }

    static HttpResponseMessage Media(HttpRequestMessage request, Stored stored)
    {
        var total = stored.Data.LongLength;
        var from = 0L;
        var to = total - 1;
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range is not null)
        {
            from = range.From ?? 0;
            to = Math.Min(range.To ?? to, total - 1);
        }

        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(stored.Data, (int)from, (int)Math.Max(0, to - from + 1))
        };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, total);
        response.Headers.Add("x-goog-generation", stored.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    static Dictionary<string, object?> Describe(string bucket, string name, Stored stored) => new()
    {
        ["kind"] = "storage#object",
        ["bucket"] = bucket,
        ["name"] = name,
        ["generation"] = stored.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["size"] = stored.Data.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["kmsKeyName"] = stored.KmsKey,
        ["metadata"] = stored.Metadata
    };

    static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        Json(status, new { error = new { code = (int)status, message } });
}
