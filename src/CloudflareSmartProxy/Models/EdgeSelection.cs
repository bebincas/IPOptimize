using System.Net;

namespace CloudflareSmartProxy.Models;

public sealed record EdgeSelection(IReadOnlyList<IPAddress> Addresses)
{
    public IPAddress? ElementAtOrDefault(int index) => index >= 0 && index < Addresses.Count ? Addresses[index] : null;
}
