using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GetAzureCosts
{
    class Program
    {
        static int resultcount = 0;

        static async Task Main(string[] args)
        {
            if (args.Length != 8)
            {
                Log("Usage: <tenantId> <clientId> <clientSecret> <startDate> <endDate> <elasticUrl> <elasticUsername> <elasticPassword>", ConsoleColor.Red);
                return;
            }

            string tenantId = args[0];
            string clientId = args[1];
            string clientSecret = args[2];
            DateTime startDate = DateTime.Parse(args[3]);
            DateTime endDate = DateTime.Parse(args[4]);
            string elasticUrl = args[5];
            string elasticUsername = args[6];
            string elasticPassword = args[7];

            var watch = Stopwatch.StartNew();

            await DoStuff(tenantId, clientId, clientSecret, startDate, endDate, elasticUrl, elasticUsername, elasticPassword);

            Log($"Done: {watch.Elapsed}", ConsoleColor.Green);
        }

        static async Task DoStuff(string tenantId, string clientId, string clientSecret, DateTime startDate, DateTime endDate, string elasticUrl, string elasticUsername, string elasticPassword)
        {
            DateTime today = DateTime.Today;

            if (startDate >= today)
            {
                startDate = today.AddDays(-1);
                Log($"start date cannot be today or in the future, instead using yesterday ({startDate.ToString("yyyy-MM-dd")}).");
            }
            if (endDate > today)
            {
                endDate = today;
                Log($"end date cannot be in the future, instead using today ({endDate.ToString("yyyy-MM-dd")}).");
            }

            JArray rates, usages;

            if (File.Exists("rates.json") && File.Exists("usages.json"))
            {
                rates = JArray.Parse(File.ReadAllText("rates.json"));
                usages = JArray.Parse(File.ReadAllText("usages.json"));
            }
            else
            {
                string accessToken = await GetAzureAccessTokensAsync(tenantId, clientId, clientSecret);
                //Log($"AccessToken: '{accessToken}'");

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    client.BaseAddress = new Uri("https://management.azure.com");
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var subscriptions = await GetSubscriptions(client);
                    rates = await GetRates(client, subscriptions);
                    usages = await GetUsages(client, subscriptions, startDate, endDate);
                }
            }

            var costs = CalculateCosts(rates, usages);

            await SaveToElastic(elasticUrl, elasticUsername, elasticPassword, costs);
        }

        static async Task<JArray> GetSubscriptions(HttpClient client)
        {
            string getSubscriptionsUrl = "/subscriptions?api-version=2016-06-01";

            Log($"Getting: '{getSubscriptionsUrl}'");
            dynamic result = await GetHttpStringAsync(client, getSubscriptionsUrl);
            JArray subscriptions = result.value;

            Log($"Found {subscriptions.Count} subscriptions.");

            return subscriptions;
        }

        static async Task<JArray> GetRates(HttpClient client, JArray subscriptions)
        {
            JArray rates = new JArray();

            foreach (dynamic subscription in subscriptions)
            {
                string subscriptionId = subscription.id;
                string subscriptionName = subscription.displayName;

                // https://msdn.microsoft.com/en-us/library/azure/mt219005
                string filter = "OfferDurableId eq 'MS-AZR-0121p' and Currency eq 'SEK' and Locale eq 'en-US' and RegionInfo eq 'SE'";
                string getCostsUrl = $"{subscriptionId}/providers/Microsoft.Commerce/RateCard?api-version=2016-08-31-preview&$filter={filter}";

                Log($"Getting: '{getCostsUrl}'");
                dynamic result = await GetHttpStringAsync(client, getCostsUrl);
                if (result == null)
                {
                    continue;
                }

                JArray offerTerms = result.OfferTerms;
                Log($"{subscriptionName}: Found {offerTerms.Count} OfferTerms.");
                JArray meters = result.Meters;
                Log($"{subscriptionName}: Found {meters.Count} Meters.");

                foreach (var meter in result.Meters)
                {
                    rates.Add(meter);
                }
            }

            return rates;
        }

        static async Task<JArray> GetUsages(HttpClient client, JArray subscriptions, DateTime startDate, DateTime endDate)
        {
            JArray usages = new JArray();

            foreach (dynamic subscription in subscriptions)
            {
                string subscriptionId = subscription.id;
                string subscriptionName = subscription.displayName;

                string getCostsUrl = $"{subscriptionId}/providers/Microsoft.Commerce/UsageAggregates?api-version=2015-06-01-preview&" +
                    $"reportedstarttime={startDate.ToString("yyyy-MM-dd")}&reportedendtime={endDate.ToString("yyyy-MM-dd")}";

                for (int page = 1; getCostsUrl != null; page++)
                {
                    Log($"Getting: '{getCostsUrl}'");
                    dynamic result = await GetHttpStringAsync(client, getCostsUrl);
                    if (result != null && result.value != null && result.value.Count > 0)
                    {
                        foreach (JObject value in result.value)
                        {
                            dynamic prettyValue = value;

                            if (prettyValue.properties != null && prettyValue.properties.instanceData != null)
                            {
                                prettyValue.properties.instanceData = JToken.Parse(prettyValue.properties.instanceData.ToString());
                            }

                            usages.Add(prettyValue);
                        }
                    }

                    if (result == null || result.nextLink == null || result.value == null || result.value.Count == null || result.value.Count == 0)
                    {
                        if (usages.Count == 0)
                        {
                            Log("Got no values.");
                        }

                        getCostsUrl = null;
                    }
                    else
                    {
                        string domain = "https://management.azure.com:443";
                        getCostsUrl = result.nextLink;
                        if (getCostsUrl.StartsWith(domain))
                        {
                            getCostsUrl = getCostsUrl.Substring(domain.Length);
                        }
                    }
                }
            }

            return usages;
        }

        static JArray CalculateCosts(JArray rates, JArray usages)
        {
            var ratesDic = new Dictionary<string, JObject>();

            foreach (dynamic rate in rates)
            {
                string a = rate.MeterId;
                JObject b = rate;

                ratesDic.Add(a, b);
            }

            foreach (dynamic usage in usages)
            {
                string meterid = usage.properties.meterId;
                JObject a = ratesDic[meterid];
                JObject b = usage;

                usage.cost = GetCost(a, b);
            }

            File.WriteAllText("rates.json", rates.ToString());
            File.WriteAllText("usages.json", usages.ToString());

            return usages;
        }

        static double GetCost(JObject rate, JObject usage)
        {
            double quantity = ((dynamic)usage).properties.quantity;

            JObject meterRates = ((dynamic)rate).MeterRates;

            var rateEntry = meterRates.Children<JProperty>()
                .Where(r => double.Parse(r.Name, CultureInfo.InvariantCulture) < quantity)
                .OrderByDescending(r => double.Parse(r.Name, CultureInfo.InvariantCulture))
                .FirstOrDefault();

            //Log($">>>{rateEntry}<<<");
            //Log($">>>{rateEntry.Value}<<<");

            double d = rateEntry.Value.ToObject<double>();

            return quantity * d;
        }

        static async Task SaveToElastic(string elasticUrl, string elasticUsername, string elasticPassword, JArray costs)
        {
            for (int i = 0; i < costs.Count; i += 10000)
            {
                var jsonrows = new List<ElasticBulkDocument>();
                foreach (JObject cost in costs.Skip(i).Take(10000))
                {
                    jsonrows.Add(new ElasticBulkDocument { Index = "costs", Id = GetHashString(cost.ToString()), Type = "doc", Document = cost });
                }
                await Elastic.PutIntoIndex(elasticUrl, elasticUsername, elasticPassword, jsonrows.ToArray());
            }
        }

        static string GetHashString(string value)
        {
            using (var crypto = new SHA256Managed())
            {
                return string.Concat(crypto.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
            }
        }

        static async Task<string> GetAzureAccessTokensAsync(string tenantId, string clientId, string clientSecret)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                string loginurl = "https://login.microsoftonline.com";
                string managementurlForAuth = "https://management.core.windows.net/";

                string url = $"{loginurl}/{tenantId}/oauth2/token?api-version=1.0";
                string data =
                    $"grant_type=client_credentials&" +
                    $"resource={WebUtility.UrlEncode(managementurlForAuth)}&" +
                    $"client_id={WebUtility.UrlEncode(clientId)}&" +
                    $"client_secret={WebUtility.UrlEncode(clientSecret)}";

                try
                {
                    dynamic result = await PostHttpStringAsync(client, url, data, "application/x-www-form-urlencoded");

                    return result.access_token.Value;
                }
                catch (HttpRequestException ex)
                {
                    Log($"Couldn't get access token for client {ex.Message}");
                    throw;
                }
            }
        }

        static async Task<JObject> GetHttpStringAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            string result = await response.Content.ReadAsStringAsync();

            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                Log(ex.Message);
                Log($"Result: '{result}'");
                return null;
            }

            if (result.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RestDebug")))
                {
                    File.WriteAllText($"result_{resultcount++}.json", JToken.Parse(result).ToString());
                }
                return JObject.Parse(result);
            }

            return null;
        }

        static async Task<JObject> PostHttpStringAsync(HttpClient client, string url, string content, string contenttype)
        {
            var response = await client.PostAsync(url, new StringContent(content, Encoding.UTF8, contenttype));
            response.EnsureSuccessStatusCode();
            string result = await response.Content.ReadAsStringAsync();

            if (result.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RestDebug")))
                {
                    File.WriteAllText($"result_{resultcount++}.json", JToken.Parse(result).ToString());
                }
                return JObject.Parse(result);
            }

            return null;
        }

        static void Log(string message, ConsoleColor color)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine(message);
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
        }

        static void Log(string message)
        {
            Console.WriteLine(message);
        }
    }
}
