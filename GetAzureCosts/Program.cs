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

        static async Task<int> Main(string[] args)
        {
            var parsedArgs = args.TakeWhile(a => a != "--").ToArray();

            if (parsedArgs.Length != 9)
            {
                Log("Usage: <tenantId> <clientId> <clientSecret> <startDate> <endDate> <offerId> <elasticUrl> <elasticUsername> <elasticPassword>", ConsoleColor.Red);
                return 1;
            }

            string tenantId = parsedArgs[0];
            string clientId = parsedArgs[1];
            string clientSecret = parsedArgs[2];
            DateTime startDate = DateTime.Parse(parsedArgs[3]);
            DateTime endDate = DateTime.Parse(parsedArgs[4]);
            string offerId = parsedArgs[5];
            string elasticUrl = parsedArgs[6];
            string elasticUsername = parsedArgs[7];
            string elasticPassword = parsedArgs[8];

            var watch = Stopwatch.StartNew();

            await DoStuff(tenantId, clientId, clientSecret, startDate, endDate, offerId, elasticUrl, elasticUsername, elasticPassword);

            Log($"Done: {watch.Elapsed}", ConsoleColor.Green);

            return 0;
        }

        static async Task DoStuff(string tenantId, string clientId, string clientSecret, DateTime startDate, DateTime endDate, string offerId, string elasticUrl, string elasticUsername, string elasticPassword)
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

            JArray subscriptions, rates, usages;

            string subscriptionsFilename = $"subscriptions_{clientId}.json";
            string ratesFilename = $"rates_{clientId}.json";
            string usagesFilename = $"usages_{clientId}.json";

            if (File.Exists(subscriptionsFilename) && File.Exists($"rates_{clientId}.json") && File.Exists($"usages_{clientId}.json"))
            {
                Log($"Reading: '{subscriptionsFilename}'");
                subscriptions = JArray.Parse(File.ReadAllText(subscriptionsFilename));
                Log($"Reading: '{ratesFilename}'");
                rates = JArray.Parse(File.ReadAllText(ratesFilename));
                Log($"Reading: '{usagesFilename}'");
                usages = JArray.Parse(File.ReadAllText(usagesFilename));
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

                    subscriptions = await GetSubscriptions(client);
                    rates = await GetRates(client, subscriptions, offerId);
                    usages = await GetUsages(client, subscriptions, startDate, endDate);
                }
            }

            var subscriptionNames = GetSubscriptionNames(subscriptions);

            Log($"Got {rates.Count} rates.");
            Log($"Got {usages.Count} usages.");

            RemoveDuplicates(usages);

            AddSubscriptionNames(usages, subscriptionNames);

            var costs = CalculateCosts(rates, usages);

            File.WriteAllText(subscriptionsFilename, subscriptions.ToString());
            File.WriteAllText(ratesFilename, rates.ToString());
            File.WriteAllText(usagesFilename, usages.ToString());

            await SaveToElastic(elasticUrl, elasticUsername, elasticPassword, costs);
        }

        static Dictionary<string, string> GetSubscriptionNames(JArray subscriptions)
        {
            var subscriptionNames = new Dictionary<string, string>();

            foreach (dynamic subscription in subscriptions)
            {
                string id = subscription.subscriptionId;
                if (id == null)
                {
                    Log($"Ignoring name from malformed subscription: '{subscription}'");
                    continue;
                }

                string name = subscription.displayName;
                if (name == null)
                {
                    Log($"Ignoring name from malformed subscription: '{subscription}'");
                    continue;
                }

                Log($"Got subscription: {id}='{name}'");
                subscriptionNames.Add(id, name);
            }

            return subscriptionNames;
        }

        static void AddSubscriptionNames(JArray usages, Dictionary<string, string> subscriptionNames)
        {
            long missingProperties = 0;
            long missingSubscriptionId = 0;
            long missingSubscriptionName = 0;
            var addedNames = new Dictionary<string, long>();
            var missingIds = new Dictionary<string, long>();

            foreach (dynamic usage in usages)
            {
                if (usage.properties != null)
                {
                    if (usage.properties.subscriptionId != null)
                    {
                        string id = usage.properties.subscriptionId;
                        if (subscriptionNames.ContainsKey(id))
                        {
                            string name = subscriptionNames[id];
                            usage.properties.subscriptionName = name;
                            if (addedNames.ContainsKey($"{name} ({id})"))
                            {
                                addedNames[$"{name} ({id})"]++;
                            }
                            else
                            {
                                addedNames[$"{name} ({id})"] = 1;
                            }
                        }
                        else
                        {
                            missingSubscriptionName++;
                            if (missingIds.ContainsKey(id))
                            {
                                missingIds[id]++;
                            }
                            else
                            {
                                missingIds[id] = 1;
                            }
                        }
                    }
                    else
                    {
                        missingSubscriptionId++;
                    }
                }
                else
                {
                    missingProperties++;
                }
            }

            Log("SubscriptionAnnotation:");
            Log($"  Missing properties: {missingProperties}");
            Log($"  Missing subscriptionId: {missingSubscriptionId}");
            Log($"  Missing missingSubscriptionName: {missingSubscriptionName}");
            foreach (var name in addedNames)
            {
                Log($"  Added names: {name.Key}: {name.Value}");
            }
            foreach (var id in missingIds)
            {
                Log($"  Missing names: {id.Key}: {id.Value}");
            }
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

        static async Task<JArray> GetRates(HttpClient client, JArray subscriptions, string offerId)
        {
            JArray rates = new JArray();

            foreach (dynamic subscription in subscriptions)
            {
                string subscriptionId = subscription.id;
                string subscriptionName = subscription.displayName;

                // https://msdn.microsoft.com/en-us/library/azure/mt219005
                string filter = $"OfferDurableId eq '{offerId}' and Currency eq 'SEK' and Locale eq 'en-US' and RegionInfo eq 'SE'";
                string getCostsUrl = $"{subscriptionId}/providers/Microsoft.Commerce/RateCard?api-version=2016-08-31-preview&$filter={filter}";

                Log($"Getting: '{getCostsUrl}'");
                dynamic result = await GetHttpStringAsync(client, getCostsUrl);
                if (result == null)
                {
                    continue;
                }

                foreach (var meter in result.Meters)
                {
                    rates.Add(meter);
                }
            }

            return rates;
        }

        static async Task<JArray> GetUsages(HttpClient client, JArray subscriptions, DateTime startDate, DateTime endDate)
        {
            var watch = Stopwatch.StartNew();

            JArray usages = new JArray();

            foreach (dynamic subscription in subscriptions)
            {
                string subscriptionId = subscription.id;
                string subscriptionName = subscription.displayName;

                string getCostsUrl = $"{subscriptionId}/providers/Microsoft.Commerce/UsageAggregates?api-version=2015-06-01-preview&" +
                    $"reportedstarttime={startDate.ToString("yyyy-MM-dd")}&reportedendtime={endDate.ToString("yyyy-MM-dd")}";

                Log($"Getting: '{getCostsUrl}'");
                for (int page = 1; getCostsUrl != null; page++)
                {
                    Console.Write('.');
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
                Log(string.Empty);
            }

            Log($"Done: {watch.Elapsed}", ConsoleColor.Cyan);

            return usages;
        }

        static void RemoveDuplicates(JArray array)
        {
            int duplicates = 0;
            var tokens = new Dictionary<string, JToken>();

            for (int i = 0; i < array.Count;)
            {
                string content = array[i].ToString();
                string hash = GetHashString(content);
                if (tokens.ContainsKey(hash))
                {
                    array[i].Remove();
                    duplicates++;
                    continue;
                }
                tokens[hash] = content;
                i++;
            }

            Log($"Duplicates: {duplicates}");
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
            int duplicates = 0;
            var ids = new Dictionary<string, string>();

            int batchsize = 10000;
            for (int i = 0; i < costs.Count; i += batchsize)
            {
                var jsonrows = new List<ElasticBulkDocument>();
                foreach (dynamic cost in costs.Skip(i).Take(batchsize))
                {
                    if (cost.properties == null || cost.properties.usageStartTime == null)
                    {
                        Log($"Invalid cost (missing usageStartTime): {cost.ToString()}");
                        continue;
                    }
                    string value = cost.properties.usageStartTime;
                    if (!DateTime.TryParse(value, out DateTime usageStartTime))
                    {
                        Log($"Invalid cost (invalid usageStartTime): {cost.ToString()}");
                        continue;
                    }

                    string id = GetHashString(cost.ToString());
                    if (ids.ContainsKey(id))
                    {
                        duplicates++;
                    }
                    ids[id] = cost.ToString();

                    jsonrows.Add(new ElasticBulkDocument { Index = $"azurecosts-{usageStartTime:yyyy.MM}", Id = id, Type = "doc", Document = cost });
                }
                await Elastic.PutIntoIndex(elasticUrl, elasticUsername, elasticPassword, jsonrows.ToArray());
            }

            Log($"Duplicates: {duplicates}");
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
