using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace GetAzureCosts
{
    public class JsonProcessor
    {
        public static void Lowercase(JArray elements, string[] fieldnames)
        {
            foreach (var element in elements)
            {
                foreach (var fieldname in fieldnames)
                {
                    var tokens = element.SelectTokens(fieldname);
                    foreach (var token in tokens)
                    {
                        if (token is JValue value)
                        {
                            value.Value = value.Value.ToString().ToLower();
                        }
                    }
                }
            }
        }
    }
}
