using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MbaseMultiSessionQwen
{
    public static class Util
    {
        public static string Env(string key, string def)
            => Environment.GetEnvironmentVariable(key) ?? def;
    }
}
