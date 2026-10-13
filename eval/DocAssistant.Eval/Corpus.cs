using System.Net.Http.Headers;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Identity;

namespace DocAssistant.Eval;

// One set of sample documents, uploaded the way a user would: a company registers, its
// admin uploads the files, and the app's own worker parses, chunks and embeds them.
//
// PDF and DOCX get a company each (docs/decisions.md #27): uploaded together, every text
// would be in the database twice and the two formats could not be told apart in the results.
//
// AccessToken is the admin's, for asking questions as that user (AnswerMeasurement).
internal sealed record Corpus(string Format, Guid TenantId, int Documents, string AccessToken)
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(10);

    public static async Task<Corpus> UploadAsync(EvalApi api, RepoPaths paths, string format, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(paths.SampleDocuments, $"*.{format}").Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No .{format} files in {paths.SampleDocuments}.");
        }

        var client = api.CreateClient();

        var registration = await client.PostAsJsonAsync(
            "/auth/register",
            new RegisterRequest
            {
                CompanyName = $"Evaluation ({format})",
                Email = $"evaluation-{format}@example.com",
                Password = "Evaluation-run-1!",
            },
            cancellationToken);
        var token = await ReadAsync<AccessToken>(registration, cancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var me = await ReadAsync<CurrentUserResponse>(await client.GetAsync("/auth/me", cancellationToken), cancellationToken);
        var tenantId = me.TenantId ?? throw new InvalidOperationException("The registered user has no organization.");

        var documentIds = new List<Guid>();
        foreach (var file in files)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(file, cancellationToken)), "file", Path.GetFileName(file));

            var uploaded = await ReadAsync<DocumentResponse>(await client.PostAsync("/documents", form, cancellationToken), cancellationToken);
            documentIds.Add(uploaded.Id);
        }

        // The upload answers before processing starts; wait for the worker to finish.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessingTimeout);

        foreach (var documentId in documentIds)
        {
            while (true)
            {
                var document = await ReadAsync<DocumentResponse>(
                    await client.GetAsync($"/documents/{documentId}", timeout.Token), timeout.Token);

                if (document.Status == nameof(DocumentStatus.Done))
                {
                    break;
                }

                if (document.Status == nameof(DocumentStatus.Failed))
                {
                    throw new InvalidOperationException($"{document.FileName} could not be processed: {document.FailureReason}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
        }

        return new Corpus(format, tenantId, documentIds.Count, token.Token);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}: {body}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("The response had no body.");
    }
}
