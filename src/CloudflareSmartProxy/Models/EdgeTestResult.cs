using System.Net;

namespace CloudflareSmartProxy.Models;

public sealed class EdgeTestResult
{
    public required IPAddress Address { get; init; }
    public string IpAddress => Address.ToString();
    public double SuccessRate { get; set; }
    public double MedianLatencyMs { get; set; } = double.PositiveInfinity;
    public double JitterMs { get; set; } = double.PositiveInfinity;
    public double DownloadMbps { get; set; }
    public double Score { get; set; }
    public string Status { get; set; } = "待测速";
    public string SuccessRateText => $"{SuccessRate:P0}";
    public string LatencyText => double.IsFinite(MedianLatencyMs) ? $"{MedianLatencyMs:F0} ms" : "—";
    public string JitterText => double.IsFinite(JitterMs) ? $"{JitterMs:F0} ms" : "—";
    public string DownloadSpeedText => DownloadMbps > 0 ? $"{DownloadMbps:F1} Mbps" : "—";
}
