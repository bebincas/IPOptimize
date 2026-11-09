using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Utilities
{
    public static class Function
    {
        public static int ParseStatusCode(string response)
        {
            var end = response.IndexOf("\r\n");

            if (end <= 0)
                return 0;

            var firstLine =
                response.Substring(0, end);

            var parts =
                firstLine.Split(' ');

            if (parts.Length >= 2 &&
                int.TryParse(parts[1], out var code))
            {
                return code;
            }

            return 0;
        }
    }
}
