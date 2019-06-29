using Newtonsoft.Json.Linq;

namespace GetAzureCosts
{
    class ElasticBulkDocument
    {
        public string Index { get; set; } = null;
        public JObject Document { get; set; } = null;
    }
}