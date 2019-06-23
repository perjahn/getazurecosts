using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace GetAzureCosts
{
    class Program
    {
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

            var costs = await RetrieveCostsFromAzure(tenantId, clientId, clientSecret, offerId, startDate, endDate);

            await SaveToElastic(elasticUrl, elasticUsername, elasticPassword, costs);
            Log($"Done: {watch.Elapsed}", ConsoleColor.Green);

            return 0;
        }

        static async Task<JArray> RetrieveCostsFromAzure(string tenantId, string clientId, string clientSecret, string offerId, DateTime startDate, DateTime endDate)
        {
            JArray subscriptions, rates, usages;

            string subscriptionsFilename = $"subscriptions_{clientId}.json";
            string ratesFilename = $"rates_{clientId}.json";
            string usagesFilename = $"usages_{clientId}.json";

            var azureCosts = new AzureCosts();

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
                string accessToken = await azureCosts.GetAzureAccessTokenAsync(tenantId, clientId, clientSecret);
                //Log($"AccessToken: '{accessToken}'");

                using (var client = new HttpClient())
                {
                    string domain = "https://management.azure.com";
                    Log($"Using domain: '{domain}'");

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    client.BaseAddress = new Uri(domain);
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    subscriptions = await azureCosts.GetSubscriptions(client);
                    rates = await azureCosts.GetRates(client, subscriptions, offerId);
                    usages = await azureCosts.GetUsages(client, subscriptions, startDate, endDate);
                }
            }

            var subscriptionNames = azureCosts.GetSubscriptionNames(subscriptions);

            Log($"Got {rates.Count} rates.");
            Log($"Got {usages.Count} usages.");

            azureCosts.AddSubscriptionNames(usages, subscriptionNames);

            var costs = azureCosts.CalculateCosts(rates, usages);

            File.WriteAllText(subscriptionsFilename, subscriptions.ToString());
            File.WriteAllText(ratesFilename, rates.ToString());
            File.WriteAllText(usagesFilename, usages.ToString());

            return costs;
        }

        static async Task SaveToElastic(string elasticUrl, string elasticUsername, string elasticPassword, JArray costs)
        {
            int duplicates = 0;

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

                    jsonrows.Add(new ElasticBulkDocument { Index = $"azurecosts-{usageStartTime:yyyy.MM}", Type = "doc", Document = cost });
                }
                await Elastic.PutIntoIndex(elasticUrl, elasticUsername, elasticPassword, jsonrows.ToArray());
            }

            Log($"Duplicates: {duplicates}");
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
