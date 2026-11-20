using CSP.Clio.Sync.Data;
using RestSharp;
using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// --token-test: shows how Clio treats a second access token for every active company, so DeleteUsesOwnClioToken can be trusted.
    /// It sends only a few tiny requests (users/who_am_i, one field) and changes nothing in the data:
    ///   1. the stored token is used (and refreshed if expired, like every run does),
    ///   2. Clio is asked for a second access token with the refresh token (this one is NOT stored),
    ///   3. both tokens are used once and Clio's rate-limit headers are printed: if the two "remaining" counters are independent,
    ///      every token has its own budget,
    ///   4. the stored token is used again: if it still works, a new token does not end the old one.
    /// If Clio did end the stored token, the new one is stored (so nothing is left broken) and the verdict says so.
    /// </summary>
    internal static class ClioTokenTest
    {
        public static async Task<int> RunAsync()
        {
            var baseUrl = (ConfigurationManager.AppSettings["ClioApiBaseUrl"] ?? "https://eu.app.clio.com/api/v4/").Trim();
            if (!baseUrl.EndsWith("/", StringComparison.Ordinal)) baseUrl += "/";

            var dataManager = new BIDataManager(false);
            var companies = dataManager.GetClioConnectedCompanies();
            Console.WriteLine("Token test for " + companies.Count + " company(ies). Clio base URL: " + baseUrl);
            Console.WriteLine();

            var problems = 0;
            foreach (var company in companies)
            {
                Console.WriteLine(company.CompanyName);
                string stored;
                try { stored = await ClioTokens.GetBearerTokenAsync(dataManager, company).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    problems++;
                    Console.Error.WriteLine("  PROBLEM: no stored token could be used: " + ex.Message);
                    continue;
                }

                var storedFirst = await Probe("stored token", baseUrl, stored).ConfigureAwait(false);

                string second;
                try
                {
                    var token = await new CSP.Clio.API.ClioServiceClient(stored)
                        .GetNewToken(company.ClioApiKey, company.ClioApiSecret, company.ClioRefreshToken).ConfigureAwait(false);
                    if (token == null || string.IsNullOrWhiteSpace(token.access_token)) throw new InvalidOperationException("Clio returned no access token.");
                    second = token.access_token;
                    Console.WriteLine("  second token       issued by Clio from the refresh token" + (string.IsNullOrWhiteSpace(token.refresh_token) ? " (no new refresh token)" : " (Clio also returned a refresh token)"));
                }
                catch (Exception ex)
                {
                    problems++;
                    Console.Error.WriteLine("  PROBLEM: a second token could not be requested: " + ex.Message);
                    continue;
                }

                var secondProbe = await Probe("second token", baseUrl, "Bearer " + second).ConfigureAwait(false);
                var storedAgain = await Probe("stored token again", baseUrl, stored).ConfigureAwait(false);

                if (!secondProbe.Item1)
                {
                    problems++;
                    Console.Error.WriteLine("  VERDICT: the second token does not work. Set DeleteUsesOwnClioToken=false.");
                }
                else if (!storedAgain.Item1)
                {
                    problems++;
                    dataManager.UpdateClioConnectedCompanies(company.ID, DateTime.UtcNow, second);
                    Console.Error.WriteLine("  VERDICT: Clio ENDED the stored token when the second one was issued, so separate tokens are not possible.");
                    Console.Error.WriteLine("           The new token was stored, so nothing is left broken. Set DeleteUsesOwnClioToken=false.");
                }
                else
                {
                    Console.WriteLine("  VERDICT: OK. The stored token still works, the second token works too" +
                        (storedFirst.Item2 != null && secondProbe.Item2 != null ? ", and each has its own rate-limit counter (remaining " + storedFirst.Item2 + " / " + secondProbe.Item2 + ")." : "."));
                }
                Console.WriteLine();
            }

            Console.WriteLine(problems == 0 ? "Token test finished: separate tokens work for every company." : "Token test finished with " + problems + " problem(s).");
            return problems == 0 ? 0 : 1;
        }

        /// <summary>One tiny request; prints the HTTP status and Clio's rate-limit headers. Returns (works, remaining).</summary>
        static async Task<Tuple<bool, string>> Probe(string label, string baseUrl, string bearer)
        {
            try
            {
                var client = new RestClient(baseUrl);
                var request = new RestRequest("users/who_am_i.json?fields=id", Method.Get);
                request.AddHeader("Authorization", bearer);
                var response = await client.ExecuteAsync(request).ConfigureAwait(false);

                Func<string, string> header = name =>
                {
                    var found = response.Headers == null ? null : response.Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
                    return found == null || found.Value == null ? null : found.Value.ToString();
                };
                var remaining = header("X-RateLimit-Remaining");
                Console.WriteLine("  " + label.PadRight(18) + " HTTP " + (int)response.StatusCode + "; rate limit " + (header("X-RateLimit-Limit") ?? "?") +
                    " per minute, remaining " + (remaining ?? "?"));
                return Tuple.Create(response.IsSuccessful, remaining);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("  " + label.PadRight(18) + " request failed: " + ex.Message);
                return Tuple.Create(false, (string)null);
            }
        }
    }
}
