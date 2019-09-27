using GetAzureCosts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace GetAzureCostsTests
{
    public class JsonProcessorTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestLowercase()
        {
            var array = new JArray
            {
                new JObject(new JProperty("aa", "BB"))
            };

            JsonProcessor.Lowercase(array, "aa".Split(','));

            string expected =
            @"[
  {
    ""aa"": ""bb""
  }
]";

            Assert.AreEqual(expected, array.ToString());
        }

        [Test]
        public void TestLowercase2()
        {
            var array = new JArray
            {
                new JObject(new JProperty("aa", new JObject(new JProperty("bb", "CC"))))
            };

            JsonProcessor.Lowercase(array, "aa.bb".Split(','));

            string expected =
            @"[
  {
    ""aa"": {
      ""bb"": ""cc""
    }
  }
]";

            Assert.AreEqual(expected, array.ToString());
        }

        [Test]
        public void TestLowercase3()
        {
            var array = new JArray
            {
                new JObject(new JProperty("properties",
                    new JObject(new JProperty("instanceData",
                        new JObject(new JProperty("Microsoft.Resources",
                            new JObject(new JProperty("resourceUri",
                                "/subscriptions/12345678-1234-1234-1234-ABCD2345678/resourceGroups/MyGroup/providers/Microsoft.Sql/servers/MySqlServer"))))))))
            };

            JsonProcessor.Lowercase(array, "properties.instanceData.['Microsoft.Resources'].resourceUri".Split(','));

            string expected =
            @"[
  {
    ""properties"": {
      ""instanceData"": {
        ""Microsoft.Resources"": {
          ""resourceUri"": ""/subscriptions/12345678-1234-1234-1234-abcd2345678/resourcegroups/mygroup/providers/microsoft.sql/servers/mysqlserver""
        }
      }
    }
  }
]";

            Assert.AreEqual(expected, array.ToString());
        }
    }
}
