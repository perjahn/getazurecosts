using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace GetAzureCosts
{
    class Elastic
    {
        static int Logcount { get; set; } = 0;

        public static async Task<JArray> GetRowsAsync(string address, string username, string password, string indexname,
            string elasticFilterField, string elasticFilterValue,
            string timestampfieldname, DateTime starttime, DateTime endtime)
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(address)
            };

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            string query;

            if (elasticFilterField != null && elasticFilterValue != null)
            {
                query =
                    "{ \"query\": { \"bool\": { \"must\": [ { \"match_phrase\": { \"" +
                    elasticFilterField +
                    "\": { \"query\": \"" +
                    elasticFilterValue +
                    "\" } } }, { \"range\": { \"" +
                    timestampfieldname +
                    "\": { \"gte\": \"" +
                    starttime.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                    "\"," + "\"lte\": \"" +
                    endtime.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                    "\" } } } ] } } }";
            }
            else
            {
                query =
                    "{ \"query\": { \"range\": { \"" +
                    timestampfieldname +
                    "\": { \"gte\": \"" +
                    starttime.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                    "\", \"lte\": \"" +
                    endtime.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                    "\" } } } }";
            }

            var url = $"{indexname}/_search?size=10000";

            Log($"url: >>>{url}<<<");
            Log($"body: >>>{query}<<<");

            WriteLogMessage(query, "post");

            string result;

            using var stringContent = new StringContent(query, Encoding.UTF8, "application/json");

            using var response = await client.PostAsync(url, stringContent);
            response.EnsureSuccessStatusCode();
            result = await response.Content.ReadAsStringAsync();

            WriteLogMessage(result, "result");

            if (result.Length > 0)
            {
                dynamic hits = JObject.Parse(result);

                return (JArray)hits.hits.hits;
            }

            return null;
        }

        public static async Task PutIntoIndex(string serverurl, string username, string password, ElasticBulkDocument[] jsonrows)
        {
            var sb = new StringBuilder();

            foreach (var jsonrow in jsonrows)
            {
                var metadata = "{ \"index\": { \"_index\": \"" + jsonrow.Index + "\" } }";
                sb.AppendLine(metadata);

                var rowdata = jsonrow.Document.ToString().Replace("\r", string.Empty).Replace("\n", string.Empty);
                sb.AppendLine(rowdata);
            }

            var address = $"{serverurl}/_bulk";
            var bulkdata = sb.ToString();

            Log($"Importing {jsonrows.Length} documents...");
            await ImportRows(address, username, password, bulkdata);

            Log("Done!");
        }

        public static async Task ImportRows(string address, string username, string password, string bulkdata)
        {
            WriteLogMessage(bulkdata, "post");

            using var client = new HttpClient();

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var content = new StringContent(bulkdata, Encoding.UTF8, "application/x-ndjson");

            // Elastic doesn't support setting charset (after encoding at Content-Type), blank it out.
            content.Headers.ContentType.CharSet = string.Empty;
            using var response = await client.PostAsync(address, content);

            var result = await response.Content.ReadAsStringAsync();
            WriteLogMessage(result, "result");
            response.EnsureSuccessStatusCode();
        }

        public static void WriteLogMessage(string message, string action)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ElasticRestDebug")))
            {
                if (TryParseJson(message, out var jtoken))
                {
                    File.WriteAllText($"Elastic_{Logcount++}_{action}.json", jtoken.ToString());
                }
                else
                {
                    File.WriteAllText($"Elastic_{Logcount++}_{action}.txt", message);
                }
            }
        }

        public static bool TryParseJson(string json, out JToken jtoken)
        {
            try
            {
                jtoken = JToken.Parse(json);
                return true;
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                jtoken = null;
                return false;
            }
        }

        static void Log(string message)
        {
            Console.WriteLine(message);
        }
    }
}