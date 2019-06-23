﻿using Newtonsoft.Json.Linq;
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
    class AzureCosts
    {
        static int Resultcount { get; set; } = 0;
        int Logcount = 0;

        public Dictionary<string, string> GetSubscriptionNames(JArray subscriptions)
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

        public void AddSubscriptionNames(JArray usages, Dictionary<string, string> subscriptionNames)
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

        public async Task<JArray> GetSubscriptions(HttpClient client)
        {
            string getSubscriptionsUrl = "/subscriptions?api-version=2016-06-01";

            dynamic result = await GetHttpStringAsync(client, getSubscriptionsUrl, null);
            JArray subscriptions = result.value;

            Log($"Found {subscriptions.Count} subscriptions.");

            return subscriptions;
        }

        public async Task<JArray> GetRates(HttpClient client, JArray subscriptions, string offerId)
        {
            JArray rates = new JArray();

            foreach (dynamic subscription in subscriptions)
            {
                string subscriptionId = subscription.id;

                // https://msdn.microsoft.com/en-us/library/azure/mt219005
                string filter = $"OfferDurableId eq '{offerId}' and Currency eq 'SEK' and Locale eq 'en-US' and RegionInfo eq 'SE'";
                string getCostsUrl = $"{subscriptionId}/providers/Microsoft.Commerce/RateCard?api-version=2016-08-31-preview&$filter={filter}";

                dynamic result = await GetHttpStringAsync(client, getCostsUrl, new[] { HttpStatusCode.Found });
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

        public async Task<JArray> GetUsages(HttpClient client, JArray subscriptions, DateTime startDate, DateTime endDate)
        {
            var watch = Stopwatch.StartNew();

            JArray usages = new JArray();

            foreach (dynamic subscription in subscriptions)
            {
                string subscriptionId = subscription.id;
                string subscriptionName = subscription.displayName;

                string getCostsUrl = $"{subscriptionId}/providers/Microsoft.Commerce/UsageAggregates?api-version=2015-06-01-preview&" +
                    $"reportedstarttime={startDate.ToString("yyyy-MM-dd")}&reportedendtime={endDate.ToString("yyyy-MM-dd")}";

                for (int page = 1; getCostsUrl != null; page++)
                {
                    Log($"Page: {page}");
                    dynamic result = await GetHttpStringAsync(client, getCostsUrl, new[] { HttpStatusCode.Found });
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
                        string validDomain = "https://management.azure.com:443";
                        getCostsUrl = result.nextLink;
                        if (getCostsUrl.StartsWith(validDomain))
                        {
                            getCostsUrl = getCostsUrl.Substring(validDomain.Length);
                        }
                        else
                        {
                            Log($"Got unknown nextLink: {getCostsUrl}");
                            return usages;
                        }
                    }
                }
            }

            Log($"Done: {watch.Elapsed}", ConsoleColor.Cyan);

            return usages;
        }

        public JArray CalculateCosts(JArray rates, JArray usages)
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

        double GetCost(JObject rate, JObject usage)
        {
            double quantity = ((dynamic)usage).properties.quantity;

            JObject meterRates = ((dynamic)rate).MeterRates;

            var rateEntry = meterRates.Children<JProperty>()
                .Where(r => double.Parse(r.Name, CultureInfo.InvariantCulture) < quantity)
                .OrderByDescending(r => double.Parse(r.Name, CultureInfo.InvariantCulture))
                .FirstOrDefault();

            double d = rateEntry.Value.ToObject<double>();

            return quantity * d;
        }

        string GetHashString(string value)
        {
            using (var crypto = new SHA256Managed())
            {
                return string.Concat(crypto.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
            }
        }

        public async Task<string> GetAzureAccessTokenAsync(string tenantId, string clientId, string clientSecret)
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

                dynamic result = await PostHttpStringAsync(client, url, data, "application/x-www-form-urlencoded");
                return result.access_token.Value;
            }
        }

        async Task<JObject> GetHttpStringAsync(HttpClient client, string url, HttpStatusCode[] semiAcceptableStatusCodes)
        {
            for (int tries = 1; tries <= 10; tries++)
            {
                string result = string.Empty;
                try
                {
                    Log($"Getting (try {tries}): '{url}'");
                    var response = await client.GetAsync(url);
                    result = await response.Content.ReadAsStringAsync();
                    if (semiAcceptableStatusCodes != null && semiAcceptableStatusCodes.Contains(response.StatusCode))
                    {
                        return null;
                    }
                    response.EnsureSuccessStatusCode();

                    if (result.Length > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RestDebug")))
                        {
                            File.WriteAllText($"result_{Logcount++}.json", JToken.Parse(result).ToString());
                        }
                        return JObject.Parse(result);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    Log($"Couldn't get url: {ex.Message}");
                    Log($"Result: '{result}'");

                    if (result.Length > 0 && result != "\"\"")
                    {
                        string filename = $"result_{Resultcount++}.html";
                        Log($"Saving result to file: {filename}");
                        SaveCrapResult(filename, result);
                    }

                    if (tries == 10)
                    {
                        throw;
                    }
                    await Task.Delay(2000);
                }
            }

            throw new Exception("Couldn't get url.");
        }

        void SaveCrapResult(string filename, string content)
        {
            string newContent = content;

            if (newContent.Length >= 2 && newContent.StartsWith("\"") && newContent.EndsWith("\""))
            {
                newContent = newContent.Substring(1, newContent.Length - 2);
            }

            newContent = newContent.Replace(@"\r", "\r").Replace(@"\n", "\n");

            File.WriteAllText(filename, newContent);
        }

        async Task<JObject> PostHttpStringAsync(HttpClient client, string url, string content, string contenttype)
        {
            var response = await client.PostAsync(url, new StringContent(content, Encoding.UTF8, contenttype));
            response.EnsureSuccessStatusCode();
            string result = await response.Content.ReadAsStringAsync();

            if (result.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RestDebug")))
                {
                    File.WriteAllText($"result_{Logcount++}.json", JToken.Parse(result).ToString());
                }
                return JObject.Parse(result);
            }

            return null;
        }

        void Log(string message, ConsoleColor color)
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

        void Log(string message)
        {
            Console.WriteLine(message);
        }
    }
}
