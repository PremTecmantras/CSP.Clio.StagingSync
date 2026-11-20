using CSP.Clio.Sync.Data;
using System;
using System.Linq;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Clio access tokens, shared by the incremental sync, the delete pass and the separate delete EXE
    /// (moved out of Program and IncrementalSyncRunner so those do not have to be linked into the delete EXE).
    /// </summary>
    internal static class ClioTokens
    {
        internal static async Task<bool> IsTokenExpired(string bearerToken)
        {
            // One tiny request decided by the HTTP status. The old check looked for the text "invalid_token" in the body and missed tokens
            // that Clio had revoked (e.g. after a second token was issued from the same refresh token), so a dead stored token was never refreshed.
            try
            {
                var baseUrl = System.Configuration.ConfigurationManager.AppSettings["ClioApiBaseUrl"];
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://eu.app.clio.com/api/v4/";
                if (!baseUrl.EndsWith("/")) baseUrl += "/";

                var client = new RestSharp.RestClient(baseUrl);
                var request = new RestSharp.RestRequest("users/who_am_i.json?fields=id", RestSharp.Method.Get);
                RestSharp.RestRequestExtensions.AddHeader(request, "Authorization", "Bearer " + bearerToken);
                var response = await client.ExecuteAsync(request).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status == 401 || status == 403) return true;
                if (status == 429) return false;           // rate limited, but the token itself is fine
                return false;   // any other answer (5xx, network): not proof that the token is dead; a refresh would only end the token other processes use
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns the bearer token of the company, refreshing it when it expired. A network blip is retried (3 tries, 15 s apart).</summary>
        internal static async Task<string> GetBearerTokenAsync(BIDataManager dataManager, C_EKPCompanies company)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await GetBearerTokenOnceAsync(dataManager, company).ConfigureAwait(false); }
                catch (Exception) when (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// A SECOND access token for the company, used only by the caller (the delete pass) and not stored. Clio limits the requests per access token
        /// (50 a minute at peak), so the delete pass, which reads about 50 pages a minute, would otherwise use up the budget of the stored token that the
        /// incremental passes need too. The new token comes from the refresh token, like the stored one did.
        ///
        /// Safety: Clio's documentation does not say whether a refresh makes the older access token stop working. After getting the new token this
        /// checks the stored one; if it no longer works, the new token is stored for everybody (as an ordinary refresh does) and the delete pass shares it,
        /// as before. Any problem getting a second token falls back to the stored token.
        /// </summary>
        internal static async Task<string> GetOwnBearerTokenAsync(BIDataManager dataManager, C_EKPCompanies company)
        {
            var stored = await GetBearerTokenAsync(dataManager, company).ConfigureAwait(false);
            try
            {
                var token = await new CSP.Clio.API.ClioServiceClient(stored)
                    .GetNewToken(company.ClioApiKey, company.ClioApiSecret, company.ClioRefreshToken).ConfigureAwait(false);
                if (token == null || string.IsNullOrWhiteSpace(token.access_token))
                {
                    Console.WriteLine("  " + company.CompanyName + ": no second Clio token could be issued; the stored token is shared.");
                    return stored;
                }

                if (await IsTokenExpired(company.ClioToken).ConfigureAwait(false))
                {
                    // The older token stopped working: separate tokens are not possible. Keep everything working with the new one.
                    company.ClioToken = token.access_token;
                    dataManager.UpdateClioConnectedCompanies(company.ID, DateTime.UtcNow, company.ClioToken);
                    Console.WriteLine("  WARNING " + company.CompanyName + ": Clio ended the older token when a new one was issued, so the new token was stored and is shared (set DeleteUsesOwnClioToken=false).");
                    return "Bearer " + token.access_token;
                }

                Console.WriteLine("  " + company.CompanyName + ": the delete pass uses its own Clio access token (its own rate limit).");
                return "Bearer " + token.access_token;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  " + company.CompanyName + ": a second Clio token could not be requested (" + ex.Message + "); the stored token is shared.");
                return stored;
            }
        }

        static async Task<string> GetBearerTokenOnceAsync(BIDataManager dataManager, C_EKPCompanies company)
        {
            // The company row may be minutes old (loaded at the start of a pass); another process may have renewed the token since.
            try
            {
                var latest = new BIDataManager(false).GetClioConnectedCompanies().FirstOrDefault(c => c.ID == company.ID);
                if (latest != null && !string.IsNullOrWhiteSpace(latest.ClioToken) && latest.ClioToken != company.ClioToken)
                    company.ClioToken = latest.ClioToken;
            }
            catch { }

            if (!await IsTokenExpired(company.ClioToken).ConfigureAwait(false))
                return "Bearer " + company.ClioToken;

            Console.WriteLine("  " + company.CompanyName + ": the stored Clio token (" + Fingerprint(company.ClioToken) + ") is not accepted; renewing it.");
            return await RefreshSharedAsync(dataManager, company, company.ClioToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Called when a request got HTTP 401 with <paramref name="failedBearer"/>: returns a working token, from the database when another process
        /// has already refreshed it, otherwise by refreshing it (once, under the lock).
        /// </summary>
        internal static Task<string> RecoverAfterUnauthorizedAsync(BIDataManager dataManager, C_EKPCompanies company, string failedBearer)
        {
            var failed = failedBearer != null && failedBearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? failedBearer.Substring(7) : failedBearer;
            return RefreshSharedAsync(dataManager, company, failed);
        }

        /// <summary>
        /// Clio ends the older access token when a new one is issued, so two processes (incremental and delete) that refresh at the same time
        /// kill each other's token. The refresh therefore happens under a lock shared by all processes of this machine, and after getting the lock
        /// the database is read again: when another process has refreshed meanwhile, its token is used and no second refresh is made.
        /// </summary>
        static async Task<string> RefreshSharedAsync(BIDataManager dataManager, C_EKPCompanies company, string deadToken)
        {
            using (await AcquireRefreshLockAsync(company.ID).ConfigureAwait(false))
            {
                C_EKPCompanies latest = null;
                try { latest = new BIDataManager(false).GetClioConnectedCompanies().FirstOrDefault(c => c.ID == company.ID); } catch { }

                if (latest != null && !string.IsNullOrWhiteSpace(latest.ClioToken) && latest.ClioToken != deadToken
                    && !await IsTokenExpired(latest.ClioToken).ConfigureAwait(false))
                {
                    company.ClioToken = latest.ClioToken;
                    Console.WriteLine("  " + company.CompanyName + ": another process already renewed the Clio token; using " + Fingerprint(company.ClioToken) + ".");
                    return "Bearer " + company.ClioToken;
                }

                var refreshToken = latest != null && !string.IsNullOrWhiteSpace(latest.ClioRefreshToken) ? latest.ClioRefreshToken : company.ClioRefreshToken;
                var tokenClient = new CSP.Clio.API.ClioServiceClient("Bearer " + deadToken);
                var token = await tokenClient.GetNewToken(company.ClioApiKey, company.ClioApiSecret, refreshToken).ConfigureAwait(false);
                if (token == null || string.IsNullOrWhiteSpace(token.access_token))
                    throw new InvalidOperationException("Clio OAuth token refresh returned no access token.");

                company.ClioToken = token.access_token;
                dataManager.UpdateClioConnectedCompanies(company.ID, DateTime.UtcNow, company.ClioToken);
                Console.WriteLine("  " + company.CompanyName + ": Clio token renewed with the refresh token (" + Fingerprint(deadToken) + " -> " + Fingerprint(company.ClioToken) + ").");
                return "Bearer " + company.ClioToken;
            }
        }

        /// <summary>Four hex characters of the SHA-256 of a token: enough to tell in the logs whether two processes use the same token, not enough to use it.</summary>
        static string Fingerprint(string token)
        {
            if (string.IsNullOrEmpty(token)) return "none";
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token)), 0, 2).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>A lock file per company that works across processes and across await (a Mutex does not). Waits up to 3 minutes.</summary>
        static async Task<IDisposable> AcquireRefreshLockAsync(Guid companyId)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csp-clio-token-" + companyId.ToString("N") + ".lock");
            var until = DateTime.UtcNow.AddMinutes(3);
            while (true)
            {
                try { return new System.IO.FileStream(path, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None); }
                catch (System.IO.IOException)
                {
                    if (DateTime.UtcNow > until) throw new InvalidOperationException("The Clio token refresh lock is held by another process.");
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }
    }
}
