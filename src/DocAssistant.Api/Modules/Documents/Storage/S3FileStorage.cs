using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace DocAssistant.Api.Modules.Documents.Storage;

// IFileStorage over the S3 API: SeaweedFS in development, any S3-compatible store in
// production (docs/decisions.md #9, #33).
public sealed class S3FileStorage(IAmazonS3 s3, IOptions<FileStorageOptions> options) : IFileStorage
{
    private readonly string _bucket = options.Value.Bucket;

    public async Task SaveAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false, // the caller owns the stream
        };

        await s3.PutObjectAsync(request, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await s3.GetObjectAsync(_bucket, key, cancellationToken);

            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            return buffer;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"No file is stored under '{key}'.", key, ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        // S3 answers a delete of a missing key with success, which is the behaviour we want.
        await s3.DeleteObjectAsync(_bucket, key, cancellationToken);
    }
}
