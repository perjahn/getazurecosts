using System.Collections.Generic;

namespace GetAzureCosts
{
    public class CmdlineArgs
    {
        public static string ExtractFlag(List<string> args, string flagname, bool delete = true)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == flagname)
                {
                    var value = args[i + 1];
                    if (delete)
                    {
                        args.RemoveAt(i);
                        args.RemoveAt(i);
                    }
                    return value;
                }
            }

            return null;
        }
    }
}
