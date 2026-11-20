using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CSP.Clio.Sync
{
    // Moved out of Program.cs unchanged so the separate delete EXE can use the same Clio paging client.
    internal sealed class ClioPageStreamMetrics
    {
        public TimeSpan ApiDownloadTime { get; set; }
        public TimeSpan RateLimitWaitTime { get; set; }
        public int PagesReceived { get; set; }
        public int RequestsAttempted { get; set; }
        public int RateLimitRetries { get; set; }
        public bool PaginationComplete { get; set; }
    }

    internal sealed class ClioPageStreamClient
    {
        const int MaximumRateLimitRetries = 5;
        readonly Uri _baseUri;

        public ClioPageStreamClient()
        {
            var configuredBaseUrl = ConfigurationManager.AppSettings["ClioApiBaseUrl"];
            if (string.IsNullOrWhiteSpace(configuredBaseUrl))
                configuredBaseUrl = "https://eu.app.clio.com/api/v4/";

            Uri baseUri;
            if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
                throw new ConfigurationErrorsException("ClioApiBaseUrl must be an absolute HTTPS URL.");

            var builder = new UriBuilder(baseUri);
            if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
                builder.Path += "/";
            _baseUri = builder.Uri;
        }

        public async Task StreamAsync(
            string apiMethodName,
            string apiService,
            string bearerToken,
            ClioPageStreamMetrics metrics,
            Func<CSP.Clio.API.Data.IDataRoot, Task> consumePage,
            string additionalQuery = null)
        {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));
            if (consumePage == null) throw new ArgumentNullException(nameof(consumePage));
            if (string.IsNullOrWhiteSpace(bearerToken)) throw new ArgumentException("A bearer token is required.", nameof(bearerToken));

            var fields = GetDefaultFields(apiMethodName);
            var pageType = GetPageType(apiMethodName);
            var nextUrl = BuildInitialUrl(apiService, fields, additionalQuery);
            var consecutiveRateLimitRetries = 0;
            var client = new RestClient(_baseUri.ToString());

            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                var request = new RestRequest(nextUrl, Method.Get);
                request.AddHeader("Authorization", bearerToken);

                RestResponse response;
                var requestTimer = Stopwatch.StartNew();
                metrics.RequestsAttempted++;
                try
                {
                    response = await client.ExecuteAsync(request).ConfigureAwait(false);
                }
                finally
                {
                    requestTimer.Stop();
                    metrics.ApiDownloadTime += requestTimer.Elapsed;
                }

                if (response == null)
                    throw new InvalidOperationException("Clio API returned no response for " + apiService + ".");

                if ((int)response.StatusCode == 429)
                {
                    if (consecutiveRateLimitRetries >= MaximumRateLimitRetries)
                        throw new InvalidOperationException("Clio API rate-limit retry limit reached for " + apiService + ".");

                    var wait = GetRetryDelay(response);
                    metrics.RateLimitRetries++;
                    var waitTimer = Stopwatch.StartNew();
                    await Task.Delay(wait).ConfigureAwait(false);
                    waitTimer.Stop();
                    metrics.RateLimitWaitTime += waitTimer.Elapsed;
                    consecutiveRateLimitRetries++;
                    continue;
                }

                if (!response.IsSuccessful)
                    throw new InvalidOperationException("Clio API request failed for " + apiService + " (HTTP " + (int)response.StatusCode + ", " + response.ResponseStatus + ").");

                var page = JsonConvert.DeserializeObject(response.Content, pageType) as CSP.Clio.API.Data.IDataRoot;
                if (page == null)
                    throw new InvalidOperationException("Clio API returned an empty or invalid page for " + apiService + ".");

                if (page.GetData() == null)
                    throw new InvalidOperationException("Clio API page had no data payload for " + apiService + ".");

                metrics.PagesReceived++;
                await consumePage(page).ConfigureAwait(false);

                consecutiveRateLimitRetries = 0;
                nextUrl = page.GetNextPageUrl();
                if (string.IsNullOrWhiteSpace(nextUrl))
                {
                    metrics.PaginationComplete = true;
                    break;
                }

                ValidateNextPageUrl(nextUrl);
            }
        }

        /// <summary>
        /// Streams only the Clio IDs of an endpoint, using the same request the extraction uses (same filters and ordering after
        /// the field list) so the ID set is directly comparable with the raw table. Used by the delete reconciliation.
        /// </summary>
        public async Task StreamIdsAsync(string apiMethodName, string apiService, string bearerToken,
            ClioPageStreamMetrics metrics, Func<List<long>, Task> consumeIds)
        {
            var defaultFields = GetDefaultFields(apiMethodName);
            var extraAt = defaultFields.IndexOf('&');
            var extras = extraAt >= 0 ? defaultFields.Substring(extraAt) : string.Empty;   // for example "&type=Matter"
            var nextUrl = _baseUri.AbsoluteUri + apiService.TrimStart('/') + "?fields=id" + extras
                + (extras.IndexOf("limit=", StringComparison.OrdinalIgnoreCase) >= 0 ? string.Empty : "&limit=200");
            ValidateNextPageUrl(nextUrl);
            var client = new RestClient(_baseUri.ToString());

            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                var response = await GetWithRateLimitRetryAsync(client, nextUrl, bearerToken, metrics, apiService).ConfigureAwait(false);
                if (!response.IsSuccessful)
                    throw new InvalidOperationException("Clio API request failed for " + apiService + " (HTTP " + (int)response.StatusCode + ").");

                var root = Newtonsoft.Json.Linq.JObject.Parse(response.Content);
                var data = root["data"] as Newtonsoft.Json.Linq.JArray;
                if (data == null)
                    throw new InvalidOperationException("Clio API page had no data payload for " + apiService + ".");

                metrics.PagesReceived++;
                await consumeIds(data.Select(item => (long)item["id"]).ToList()).ConfigureAwait(false);

                nextUrl = (string)root["meta"]?["paging"]?["next"];
                if (string.IsNullOrWhiteSpace(nextUrl))
                {
                    metrics.PaginationComplete = true;
                    break;
                }
                ValidateNextPageUrl(nextUrl);
            }
        }

        /// <summary>True when Clio still returns the record, false on HTTP 404. Anything else is an error, never "deleted".</summary>
        public async Task<bool> RecordExistsAsync(string apiService, long id, string bearerToken)
        {
            var client = new RestClient(_baseUri.ToString());
            var url = _baseUri.AbsoluteUri + apiService.TrimStart('/') + "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "?fields=id";
            ValidateNextPageUrl(url);
            var response = await GetWithRateLimitRetryAsync(client, url, bearerToken, new ClioPageStreamMetrics(), apiService).ConfigureAwait(false);
            if ((int)response.StatusCode == 404) return false;
            if (response.IsSuccessful) return true;
            throw new InvalidOperationException("Clio API check failed for " + apiService + "/" + id + " (HTTP " + (int)response.StatusCode + ").");
        }

        async Task<RestResponse> GetWithRateLimitRetryAsync(RestClient client, string url, string bearerToken,
            ClioPageStreamMetrics metrics, string apiService)
        {
            for (var attempt = 0; ; attempt++)
            {
                var request = new RestRequest(url, Method.Get);
                request.AddHeader("Authorization", bearerToken);
                var timer = Stopwatch.StartNew();
                metrics.RequestsAttempted++;
                RestResponse response;
                try { response = await client.ExecuteAsync(request).ConfigureAwait(false); }
                finally { timer.Stop(); metrics.ApiDownloadTime += timer.Elapsed; }

                if (response == null)
                    throw new InvalidOperationException("Clio API returned no response for " + apiService + ".");
                if ((int)response.StatusCode != 429)
                    return response;
                if (attempt >= MaximumRateLimitRetries)
                    throw new InvalidOperationException("Clio API rate-limit retry limit reached for " + apiService + ".");

                metrics.RateLimitRetries++;
                var wait = Stopwatch.StartNew();
                await Task.Delay(GetRetryDelay(response)).ConfigureAwait(false);
                metrics.RateLimitWaitTime += wait.Elapsed;
            }
        }

        static string GetDefaultFields(string apiMethodName)
        {
            var method = typeof(CSP.Clio.API.ClioServiceClient).GetMethod(apiMethodName, BindingFlags.Instance | BindingFlags.Public);
            var parameter = method?.GetParameters().FirstOrDefault();
            var value = parameter?.DefaultValue as string;
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("No default field selection is available for Clio API method '" + apiMethodName + "'.");
            return value;
        }

        static Type GetPageType(string apiMethodName)
        {
            var method = typeof(CSP.Clio.API.ClioServiceClient).GetMethod(apiMethodName, BindingFlags.Instance | BindingFlags.Public);
            var methodReturnType = method?.ReturnType;
            if (methodReturnType == null || !methodReturnType.IsGenericType || methodReturnType.GetGenericTypeDefinition() != typeof(Task<>))
                throw new InvalidOperationException("Unable to determine the Clio page type for API method '" + apiMethodName + "'.");

            var operationType = methodReturnType.GetGenericArguments()[0];
            var pageType = operationType.GetProperty("DataModel")?.PropertyType;
            if (pageType == null || !typeof(CSP.Clio.API.Data.IDataRoot).IsAssignableFrom(pageType))
                throw new InvalidOperationException("Clio API method '" + apiMethodName + "' does not return a supported page model.");
            return pageType;
        }

        string BuildInitialUrl(string apiService, string fields, string additionalQuery)
        {
            if (string.IsNullOrWhiteSpace(apiService) || apiService.IndexOfAny(new[] { '?', '#', '\\' }) >= 0)
                throw new ArgumentException("Invalid Clio API service path.", nameof(apiService));

            if (!string.IsNullOrWhiteSpace(additionalQuery) &&
                (additionalQuery.IndexOf('#') >= 0 || additionalQuery.IndexOf('?') >= 0 || additionalQuery.IndexOf('\\') >= 0))
                throw new ArgumentException("Invalid additional Clio API query string.", nameof(additionalQuery));

            var url = _baseUri.AbsoluteUri + apiService.TrimStart('/') + "?fields=" + fields;
            if (!string.IsNullOrWhiteSpace(additionalQuery))
                url += "&" + additionalQuery.TrimStart('&');
            ValidateNextPageUrl(url);
            return url;
        }

        void ValidateNextPageUrl(string url)
        {
            Uri next;
            if (!Uri.TryCreate(url, UriKind.Absolute, out next) || next.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(next.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) || next.Port != _baseUri.Port ||
                !next.AbsolutePath.StartsWith(_baseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Clio API returned a paging URL outside the configured HTTPS API path.");
        }

        static TimeSpan GetRetryDelay(RestResponse response)
        {
            var retryAfter = response.Headers?.FirstOrDefault(h => string.Equals(h.Name, "Retry-After", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
            int seconds;
            if (int.TryParse(retryAfter, out seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);

            var match = Regex.Match(response.Content ?? string.Empty, @"retry\s+in\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);

            return TimeSpan.FromSeconds(60);
        }
    }
}
