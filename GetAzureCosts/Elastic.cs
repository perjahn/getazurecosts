using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            using (HttpClient client = new HttpClient())
            {
                client.BaseAddress = new Uri(address);

                string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
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

                string url = $"{indexname}/_search?size=10000";

                Log($"url: >>>{url}<<<");
                Log($"body: >>>{query}<<<");

                WriteLogMessage(query, "post");

                var response = await client.PostAsync(url, new StringContent(query, Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
                string result = await response.Content.ReadAsStringAsync();

                WriteLogMessage(result, "result");

                if (result.Length > 0)
                {
                    dynamic hits = JObject.Parse(result);

                    return (JArray)hits.hits.hits;
                }
            }

            return null;
        }

        public static async Task PutIntoIndex(string serverurl, string username, string password, ElasticBulkDocument[] jsonrows)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var jsonrow in jsonrows)
            {
                string metadata = "{ \"index\": { \"_index\": \"" + jsonrow.Index + "\" } }";
                sb.AppendLine(metadata);

                string rowdata = jsonrow.Document.ToString().Replace("\r", string.Empty).Replace("\n", string.Empty);
                sb.AppendLine(rowdata);
            }

            string address = $"{serverurl}/_bulk";
            string bulkdata = sb.ToString();

            Log($"Importing {jsonrows.Length} documents...");
            await ImportRows(address, username, password, bulkdata);

            Log("Done!");
        }

        public static async Task ImportRows(string address, string username, string password, string bulkdata)
        {
            WriteLogMessage(bulkdata, "post");

            using (var client = new HttpClient())
            {
                string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var content = new StringContent(bulkdata, Encoding.UTF8, "application/x-ndjson");
                // Elastic doesn't support setting charset (after encoding at Content-Type), blank it out.
                content.Headers.ContentType.CharSet = string.Empty;
                var response = await client.PostAsync(address, content);

                string result = await response.Content.ReadAsStringAsync();
                WriteLogMessage(result, "result");

                response.EnsureSuccessStatusCode();
            }
        }

        public static void WriteLogMessage(string message, string action)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ElasticRestDebug")))
            {
                if (TryParseJson(message, out JToken jtoken))
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