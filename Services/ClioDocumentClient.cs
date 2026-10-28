using CSP.Clio.Sync.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace CSP.Clio.Sync.Services
{
    internal sealed class ClioDocumentClient : IDisposable
    {
        private const string DefaultDocumentFields = "id,name,latest_document_version{version_number},matter{id,display_number,number}";

        private readonly HttpClient _client;
        private readonly string _baseUrl;

        public ClioDocumentClient(string bearerToken, string baseUrl = "https://eu.app.clio.com/api/v4/")
        {
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                throw new ArgumentException("Clio bearer token is required.", nameof(bearerToken));
            }

            _baseUrl = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            _client = new HttpClient
            {
                // Default 100s is too tight for large unfiltered documents.json pages (e.g. when
                // updated_since can't be applied). DownloadDocumentAsync uses ResponseHeadersRead
                // so this only extends the header-wait there, not the full download.
                Timeout = TimeSpan.FromSeconds(180)
            };
            _client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(NormalizeBearerToken(bearerToken));
        }

        private static readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(3, 3); // max 3 concurrent downloads
        private const int MaxRetryAttempts = 5;
        private volatile bool _tokenExpired = false;
        private const int MAX_PATH = 260;
        public async Task<int> DownloadDocumentsForMattersAsync(
            IEnumerable<ClioMatterDocumentTarget> matters,
            string documentsRootPath,
            Action<Exception, string> logError,
            BIDataManager dataManager,
            DateTime? updatedSince = null
            )
        {

            _tokenExpired = false;

            var matterLookup = matters
                .Where(m => m != null && m.MatterId > 0)
                .GroupBy(m => m.MatterId)
                .ToDictionary(g => g.Key, g => g.First());

            if (matterLookup.Count == 0) return 0;

            const string changedDocumentFields = "id,name,updated_at,latest_document_version{version_number},matter{id,display_number,number}";
            const string deletedDocumentFields = "id,deleted_at";

            var documents = await GetDocumentsForMatterKeysAsync(
                matterLookup.Keys,
                updatedSince,
                includeDeleted: false,
                fieldsOverride: changedDocumentFields
                );

            if (_tokenExpired)
            {
                Console.WriteLine("Token expired while fetching document list. Ending this sync run early — will resume fully on next run.");
                return 0;
            }

            var deletedDocuments = await GetDocumentsForMatterKeysAsync(
                matterLookup.Keys,
               updatedSince,
                includeDeleted: true,
                fieldsOverride: deletedDocumentFields
                );

            if (_tokenExpired)
            {
                Console.WriteLine("Token expired while fetching deleted-documents list. Ending this sync run early — will resume fully on next run.");
                return 0;
            }

            foreach (var deletedDocument in deletedDocuments.Where(d => d?.deleted_at != null))
            {
                dataManager.MarkDocumentDeleted(deletedDocument.id, deleteFileFromDisk: true, logError: logError);
            }

            var matchedMatterIds = documents
                    .Where(d => d.matter != null)
                    .Select(d => d.matter.id)
                    .Distinct()
                    .ToHashSet();


            Console.WriteLine($"Fetched Matters: {matterLookup.Count}");
            Console.WriteLine($"Deleted documents found: {deletedDocuments.Count(d => d?.deleted_at != null)}");

            var syncedVersionMap = dataManager.GetSyncedDocumentVersionMap();
            var pendingDocuments = new List<ClioDocument>();

            foreach (var document in documents)
            {
                if (document?.matter == null) continue;

                var clioVersionNumber = GetClioVersionNumber(document);

                if (!syncedVersionMap.TryGetValue(document.id, out var syncedDocument))
                {
                    pendingDocuments.Add(document);
                    continue;
                }

                if (!string.Equals(syncedDocument.Status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(syncedDocument.Status, "Deleted", StringComparison.OrdinalIgnoreCase))
                {
                    pendingDocuments.Add(document);
                    continue;
                }

                if (!clioVersionNumber.HasValue)
                {
                    continue; // Clio didn't return a version number, nothing to compare or backfill
                }

                if (!syncedDocument.ClioVersionNumber.HasValue)
                {
                    // Never recorded before (e.g. pre-existing Completed doc from before this feature) —
                    // just backfill the value, don't treat as an update
                    dataManager.SetDocumentVersionNumber(document.id, clioVersionNumber.Value);
                    continue;
                }

                if (syncedDocument.ClioVersionNumber.Value != clioVersionNumber.Value)
                {
                    dataManager.MarkDocumentUpdated(document.id);
                    pendingDocuments.Add(document);
                }
            }

            Console.WriteLine($"Documents : {documents.Count - pendingDocuments.Count}, remaining to process: {pendingDocuments.Count}");

            // STEP 1: SAVE ALL PENDING DOCUMENTS TO DB FIRST

            Console.WriteLine($"Saving {pendingDocuments.Count} documents to DB as Pending...");

            foreach (var document in pendingDocuments)
            {
                if (document?.matter == null) continue;

                if (!matterLookup.TryGetValue(document.matter.id, out var matter)) continue;

                var matterFolderName = SanitizeFileName(string.IsNullOrWhiteSpace(matter.DisplayNumber)
                    ? $"{matter.MatterId}"
                    : $"{matter.MatterId}_{matter.DisplayNumber}");
                var documentFileName = SanitizeFileName($"{document.name}");

                // ✅ ENSURE PATH UNDER 260 BEFORE SAVING TO DB
                var targetPath = EnsurePathUnder260(documentsRootPath, matterFolderName, documentFileName);

                Console.WriteLine($"DocumentId {document.id} -> {targetPath} ({targetPath.Length} chars)");


                // Only upsert if NOT already completed
                dataManager.UpsertSyncedDocument(
                    document.id,
                    matter.MatterId,
                    matter.MatterNumber.Split('.')[0],
                    documentFileName,
                    Path.GetDirectoryName(targetPath),
                    "Pending",
                    "Queued for download",
                    incrementRetry: false,
                    clioVersionNumber: GetClioVersionNumber(document));
            }

            Console.WriteLine($"Saved {pendingDocuments.Count} documents to DB as Pending.");

            // STEP 2: DOWNLOAD EACH DOCUMENT

            var downloadedCount = 0;
            var tasks = new List<Task>();

            foreach (var document in pendingDocuments)
            {
                if (document?.matter == null) continue;

                if (!matterLookup.TryGetValue(document.matter.id, out var matter)) continue;

                var existingRetries = dataManager.GetRetryCount(document.id);
                if (existingRetries >= MaxRetryAttempts)
                {
                    Console.WriteLine($"Skipping DocumentId {document.id} - exceeded max retry attempts ({MaxRetryAttempts})");
                    dataManager.UpsertSyncedDocument(
                        document.id, matter.MatterId, matter.MatterNumber,
                        SanitizeFileName($"{document.name}"),
                        Path.Combine(documentsRootPath, SanitizeFileName(string.IsNullOrWhiteSpace(matter.DisplayNumber) ? $"{matter.MatterId}" : $"{matter.MatterId}_{matter.DisplayNumber}")),
                        "Failed",
                        "Exceeded max retry attempts",
                        incrementRetry: false,
                        clioVersionNumber: GetClioVersionNumber(document));
                    continue;
                }

                var matterFolderName = SanitizeFileName(string.IsNullOrWhiteSpace(matter.DisplayNumber)
                    ? $"{matter.MatterId}"
                    : $"{matter.MatterId}_{matter.DisplayNumber}");
                var documentFileName = SanitizeFileName($"{document.name}");
                var targetPath = EnsurePathUnder260(documentsRootPath, matterFolderName, documentFileName);

                var doc = document;
                var mat = matter;

                tasks.Add(Task.Run(async () =>
                {
                    if (_tokenExpired)
                    {
                        return;
                    }

                    await _downloadSemaphore.WaitAsync();
                    try
                    {
                        if (_tokenExpired)
                        {
                            return;
                        }

                        var success = await DownloadWithRetryAsync(
                            doc, targetPath, logError, mat, dataManager, existingRetries);

                        if (success)
                        {
                            Interlocked.Increment(ref downloadedCount);
                        }
                    }
                    finally
                    {
                        _downloadSemaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            return downloadedCount;
        }

        // ✅ PROPERLY TRUNCATE PATH TO STAY UNDER 260 CHARS
        private string EnsurePathUnder260(string documentsRootPath, string folderName, string fileName)
        {
            var targetPath = Path.Combine(documentsRootPath, folderName, fileName);

            if (targetPath.Length < MAX_PATH)
                return targetPath;

            Console.WriteLine($"Path too long ({targetPath.Length} chars), truncating...");

            // Available space = 260 - root path - 2 (slashes)
            int availableSpace = MAX_PATH - documentsRootPath.Length - 2;

            // Try to fit both folder and file
            int folderSpace = availableSpace / 2;  // Give 50% to folder, 50% to file
            int fileSpace = availableSpace - folderSpace;

            if (folderName.Length > folderSpace)
            {
                folderName = folderName.Substring(0, Math.Max(10, folderSpace));
            }

            if (fileName.Length > fileSpace)
            {
                fileName = TruncateFilename(fileName, Math.Max(10, fileSpace));
            }

            targetPath = Path.Combine(documentsRootPath, folderName, fileName);
            Console.WriteLine($"Adjusted to ({targetPath.Length} chars): {targetPath}");
            return targetPath;
        }

        private string TruncateFilename(string filename, int maxLength)
        {
            if (filename.Length <= maxLength)
                return filename;

            var ext = Path.GetExtension(filename);
            var name = Path.GetFileNameWithoutExtension(filename);
            var nameLength = Math.Max(5, maxLength - ext.Length);

            return name.Substring(0, Math.Min(name.Length, nameLength)) + ext;
        }

        private async System.Threading.Tasks.Task<bool> DownloadWithRetryAsync(
            ClioDocument document,
            string targetPath,
            Action<Exception, string> logError,
            ClioMatterDocumentTarget matter,
            BIDataManager dataManager,
            int alreadyAttempted
            //Action<DateTime> updateSyncTime = null
            )
        {
            var delay = TimeSpan.FromSeconds(2);
            var attemptsLeft = MaxRetryAttempts - alreadyAttempted;

            for (int attempt = 0; attempt <= attemptsLeft; attempt++)
            {
                try
                {
                    await DownloadDocumentAsync(document, targetPath);
                    dataManager.UpsertSyncedDocument(
             document.id, matter.MatterId, matter.MatterNumber, Path.GetFileName(targetPath),
             Path.GetDirectoryName(targetPath), "Completed", clioVersionNumber: GetClioVersionNumber(document),
             lastUpdatedDate: document.updated_at);
                    return true;
                }
                catch (ClioUnauthorizedException)
                {
                    // Token expired - per team decision, just mark Pending and move on.
                    // Next sync run will pick this up again with a fresh token.
                    _tokenExpired = true;
                    Console.WriteLine($"Token expired on DocumentId {document.id}, marking Pending for next run.");
                    dataManager.UpsertSyncedDocument(
                        document.id, matter.MatterId, matter.MatterNumber, Path.GetFileName(targetPath),
                        Path.GetDirectoryName(targetPath), "Pending", "Token expired, will retry next sync run", incrementRetry: false,
                        clioVersionNumber: GetClioVersionNumber(document));
                    return false; // no point retrying within this run, token won't fix itself
                }
                catch (ClioRateLimitException ex) when (attempt < attemptsLeft)
                {
                    Console.WriteLine($"Rate limited on DocumentId {document.id}, retry {attempt + 1}/{attemptsLeft} after {ex.RetryAfterSeconds}s (Clio-specified)");
                    dataManager.UpsertSyncedDocument(
                        document.id, matter.MatterId, matter.MatterNumber, Path.GetFileName(targetPath),
                        Path.GetDirectoryName(targetPath), "Pending", "Rate limited, retrying", incrementRetry: true,
                        clioVersionNumber: GetClioVersionNumber(document));
                    // Checkpoint sync time before sleeping so a long rate-limit wait is recorded
                    //updateSyncTime?.Invoke(DateTime.UtcNow);
                    await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfterSeconds));
                }
                catch (Exception ex)
                {
                    var newRetryCount = alreadyAttempted + attempt + 1;
                    var finalStatus = newRetryCount >= MaxRetryAttempts ? "Failed" : "Pending";
                    dataManager.UpsertSyncedDocument(
                        document.id, matter.MatterId, matter.MatterNumber, Path.GetFileName(targetPath),
                        Path.GetDirectoryName(targetPath), finalStatus, ex.Message, incrementRetry: true,
                        clioVersionNumber: GetClioVersionNumber(document));
                    logError?.Invoke(ex, $"Documents | DocumentId: {document.id} | MatterId: {matter.MatterId} | MatterNumber: {matter.MatterNumber}");
                    return false;
                }
            }
            dataManager.UpsertSyncedDocument(
       document.id, matter.MatterId, matter.MatterNumber, Path.GetFileName(targetPath),
       Path.GetDirectoryName(targetPath), "Failed", "Exhausted retry attempts due to rate limiting",
       clioVersionNumber: GetClioVersionNumber(document));

            return false;
        }
        public static string PrepareDocumentsDirectory(string configuredPath)
        {
            var documentsRootPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Documents")
                : configuredPath;

            var fullPath = Path.GetFullPath(documentsRootPath);
            GuardSafeDirectoryPath(fullPath);

            if (!Directory.Exists(fullPath))
            {
                //Directory.Delete(fullPath, recursive: true);
                Directory.CreateDirectory(fullPath);
            }

            Console.WriteLine($"Document directory refreshed: {fullPath}");
            return fullPath;
        }

        private async Task<List<ClioDocument>> GetDocumentsForMatterKeysAsync(
            IEnumerable<long> matterIds,
            DateTime? updatedSince = null,
            bool includeDeleted = false,
            string fieldsOverride = null
            //Action<DateTime> updateSyncTime = null
            )
        {
            var matchedDocuments = new List<ClioDocument>();
            var keys = new HashSet<long>(matterIds);
            string requestUrl = BuildDocumentsRequestUrl(updatedSince, includeDeleted, fieldsOverride);

            while (!string.IsNullOrWhiteSpace(requestUrl))
            {
                string responseContent;
                try
                {
                    responseContent = await GetStringAsync(requestUrl);
                }
                catch (ClioUnauthorizedException)
                {
                    Console.WriteLine(
                        $"Token expired while paginating documents. Returning {matchedDocuments.Count} documents collected so far; remainder will be picked up on next sync run.");
                    _tokenExpired = true;
                    break; // stop pagination here, return partial results instead of crashing
                }

                var page = JsonConvert.DeserializeObject<ClioDocumentResponse>(responseContent);

                if (page?.data != null)
                {
                    matchedDocuments.AddRange(
                        page.data.Where(d =>
                            d != null &&
                            ((d.matter != null && keys.Contains(d.matter.id)) ||
                             (includeDeleted && d.deleted_at != null))));
                }

                requestUrl = page?.meta?.paging?.next;
            }

            return matchedDocuments;
        }

        private string BuildDocumentsRequestUrl(DateTime? updatedSince, bool includeDeleted, string fieldsOverride)
        {
            var query = new List<string>
            {
                "fields=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(fieldsOverride) ? DefaultDocumentFields : fieldsOverride),
                "limit=200",
                "order=id(asc)"
            };

            if (updatedSince.HasValue)
            {
                query.Add("updated_since=" + Uri.EscapeDataString(updatedSince.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
            }

            if (includeDeleted)
            {
                query.Add("include_deleted=true");
            }

            return _baseUrl + "documents.json?" + string.Join("&", query);
        }

        private async Task DownloadDocumentAsync(ClioDocument document, string targetPath
            //Action<DateTime> updateSyncTime = null
            )
        {
            var requestUrl = $"{_baseUrl}documents/{document.id}/download.json";

            using (var response = await _client.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    throw new ClioUnauthorizedException($"Clio token expired downloading document {document.id}. Response: {error}");
                }

                if ((int)response.StatusCode == 429)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    int retrySeconds = GetRetryAfterSeconds(response, error);
                    throw new ClioRateLimitException($"Rate limited downloading document {document.id}. Retry in {retrySeconds}s. Response: {error}", retrySeconds);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Clio document download failed for document {document.id}. Status: {(int)response.StatusCode}. Response: {error}");
                }

                Console.WriteLine($"downloaded document {document.id}.");
                var dir = Path.GetDirectoryName(targetPath);
                bool folderCreated = !Directory.Exists(dir);
                Directory.CreateDirectory(dir);
                if (folderCreated)
                {
                    // Checkpoint: a new matter folder was created; record progress so the next
                    // run does not re-do the full sync if this run is interrupted.
                    Console.WriteLine($"Matter folder created: {dir}");
                    // updateSyncTime?.Invoke(DateTime.UtcNow);
                }

                // Get file size
                long contentLength = response.Content.Headers.ContentLength ?? 0;

                // Small file (< 50MB) — faster to load to memory
                if (contentLength > 0 && contentLength < 50 * 1024 * 1024)
                {
                    Console.WriteLine($"Downloaded document {document.id} ({contentLength / 1024}KB) to memory, writing to disk...");
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    await Task.Run(() => File.WriteAllBytes(targetPath, bytes));
                }
                else
                {
                    // Large file or unknown size — stream with 1MB buffer
                    Console.WriteLine($"Downloaded document {document.id}, streaming to disk...");
                    using (var source = await response.Content.ReadAsStreamAsync())
                    using (var destination = new FileStream(
                        targetPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 1024 * 1024,
                        useAsync: true))
                    {
                        await source.CopyToAsync(destination, 1024 * 1024);
                    }
                }

                Console.WriteLine($"Document {document.id} saved to {targetPath}");
            }
        }

        private static int GetRetryAfterSeconds(HttpResponseMessage response, string errorBody, int fallback = 10)
        {
            // Preferred: official Retry-After header, per Clio's documented rate-limit behavior
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                    return (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;

                if (response.Headers.RetryAfter.Date.HasValue)
                {
                    var seconds = (response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                    if (seconds > 0) return (int)seconds;
                }
            }

            // Fallback: parse it out of the error body, in case Retry-After is ever missing
            var match = System.Text.RegularExpressions.Regex.Match(errorBody, @"Retry in (\d+) second");
            return match.Success ? int.Parse(match.Groups[1].Value) : fallback;
        }

        public class ClioUnauthorizedException : Exception
        {
            public ClioUnauthorizedException(string message) : base(message) { }
        }

        public class ClioRateLimitException : Exception
        {
            public int RetryAfterSeconds { get; }
            public ClioRateLimitException(string message, int retryAfterSeconds) : base(message)
            {
                RetryAfterSeconds = retryAfterSeconds;
            }
        }

        private static bool IsTokenExpired(Exception ex)
        {
            return ex.Message.Contains("401") &&
                   ex.Message.Contains("invalid_token");
        }

        private const int MaxApiRetries = 5;

        private async Task<string> GetStringAsync(string requestUrl
            //Action<DateTime> updateSyncTime = null
            )
        {
            for (int attempt = 1; attempt <= MaxApiRetries; attempt++)
            {
                HttpResponseMessage response;
                try
                {
                    response = await _client.GetAsync(requestUrl);
                }
                catch (Exception ex) when ((ex is TaskCanceledException || ex is HttpRequestException) && attempt < MaxApiRetries)
                {
                    // Client-side timeout or transient network failure (not a real cancellation,
                    // since no CancellationToken was passed in) - back off and retry like a 429,
                    // instead of letting it escape and abort the whole sync run.
                    var waitSeconds = 10 * attempt;
                    Console.WriteLine(
                        $"Request timed out/failed ({ex.GetType().Name}). Waiting {waitSeconds}s. Attempt {attempt}/{MaxApiRetries}. Url: {requestUrl}");
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                    continue;
                }

                using (response)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        return content;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        throw new ClioUnauthorizedException(
                            $"Clio token expired. Url: {requestUrl}. Response: {content}");
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        int waitSeconds = 30;

                        var match = System.Text.RegularExpressions.Regex.Match(
                            content,
                            @"Retry in (\d+) seconds");

                        if (match.Success)
                        {
                            waitSeconds = int.Parse(match.Groups[1].Value);
                        }

                        Console.WriteLine(
                            $"Rate limited. Waiting {waitSeconds}s. Attempt {attempt}/{MaxApiRetries}");

                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));

                        continue;
                    }

                    throw new Exception(
                        $"Clio API request failed. Url: {requestUrl}. Status: {(int)response.StatusCode}. Response: {content}");
                }
            }

            throw new Exception(
                $"Exceeded maximum retry attempts ({MaxApiRetries}) for URL: {requestUrl}");
        }

        private static string NormalizeBearerToken(string token)
        {
            return token.Trim().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? token.Trim()
                : "Bearer " + token.Trim();
        }

        private static string BuildMatterKey(long matterId, string matterNumber)
        {
            return $"{matterId}|{NormalizeMatterNumber(matterNumber)}";
        }

        private static string NormalizeMatterNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return decimal.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
                ? number.ToString("0", CultureInfo.InvariantCulture)
                : value.Trim();
        }

        private static int? GetClioVersionNumber(ClioDocument document)
        {
            if (document?.latest_document_version == null)
            {
                return null;
            }

            return Convert.ToInt32(document.latest_document_version.version_number);
        }

        private static void GuardSafeDirectoryPath(string path)
        {
            var root = Path.GetPathRoot(path);

            if (string.IsNullOrWhiteSpace(path) ||
                string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length <= root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
            {
                throw new InvalidOperationException($"Unsafe document directory path: {path}");
            }
        }

        private const int MaxFileNameLength = 150; // safe margin, leaves room for folder path

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "unnamed";

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

            if (sanitized.Length > MaxFileNameLength)
            {
                var extension = Path.GetExtension(sanitized);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(sanitized);
                var maxNameLength = MaxFileNameLength - extension.Length;
                sanitized = nameWithoutExt.Substring(0, Math.Min(nameWithoutExt.Length, maxNameLength)) + extension;
            }

            return sanitized;
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    public sealed class ClioMatterDocumentTarget
    {
        public long MatterId { get; set; }
        public string MatterNumber { get; set; }
        public string DisplayNumber { get; set; }
    }

    internal sealed class ClioDocumentResponse
    {
        public List<ClioDocument> data { get; set; }
        public ClioMeta meta { get; set; }
    }

    internal sealed class ClioDocument
    {
        public long id { get; set; }
        public string name { get; set; }
        public DateTime updated_at { get; set; }
        public ClioDocumentVersion latest_document_version { get; set; }
        public ClioDocumentMatter matter { get; set; }
        public DateTime? deleted_at { get; set; }
    }

    internal sealed class ClioDocumentVersion
    {
        public long id { get; set; }
        public decimal version_number { get; set; }
    }

    internal sealed class ClioDocumentMatter
    {
        public long id { get; set; }
        public decimal? number { get; set; }
        public string display_number { get; set; }
    }

    internal sealed class ClioMeta
    {
        public ClioPaging paging { get; set; }
        public double records { get; set; }
    }

    internal sealed class ClioPaging
    {
        public string next { get; set; }
    }
    public class DocumentSyncInfo
    {
        public int NullCount { get; set; }
        public DateTime? LastUpdatedDate { get; set; }
    }
}
