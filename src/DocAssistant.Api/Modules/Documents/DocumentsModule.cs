using Amazon.Runtime;
using Amazon.S3;
using DocAssistant.Api.Modules.Documents.Storage;
using Microsoft.Extensions.Options;

namespace DocAssistant.Api.Modules.Documents;

public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services)
    {
        // Checked at startup: a missing storage setting should stop the app from starting,
        // not surface as a failed upload later.
        services.AddOptions<FileStorageOptions>()
            .BindConfiguration(FileStorageOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The client is thread-safe and meant to be reused; creating it opens no connection.
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<FileStorageOptions>>().Value;

            var config = new AmazonS3Config
            {
                ServiceURL = storage.ServiceUrl,

                // http://host/bucket/key instead of http://bucket.host/key: a self-hosted
                // endpoint such as localhost has no per-bucket DNS names.
                ForcePathStyle = true,

                // S3-compatible servers ignore the region, but request signing needs one.
                AuthenticationRegion = "us-east-1",

                // Newer SDKs add checksum trailers to every upload by default; not every
                // S3-compatible server understands them.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            };

            return new AmazonS3Client(new BasicAWSCredentials(storage.AccessKey, storage.SecretKey), config);
        });

        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }
}
