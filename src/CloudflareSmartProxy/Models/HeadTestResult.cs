using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Models
{
    /// <summary>
    /// 做HEAD请求
    /// </summary>
    public sealed class HeadTestResult
    {
        public IPAddress IP { get; init; } = null!;

        public bool Success { get; init; }

        public int StatusCode { get; init; }

        public long TotalMs { get; init; }

        public string? Error { get; init; }
    }
}
