using System.Net;
using System.Net.Sockets;

namespace CloudflareSmartProxy.Utilities;

public sealed class IpNetwork
{
    public IPAddress NetworkAddress { get; }
    public int PrefixLength { get; }

    private IpNetwork(IPAddress networkAddress, int prefixLength)
    {
        NetworkAddress = networkAddress;
        PrefixLength = prefixLength;
    }

    public static bool TryParse(string value, out IpNetwork? network)
    {
        network = null;
        var parts = value.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefix))
            return false;

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > bits)
            return false;

        network = new IpNetwork(ApplyMask(address, prefix), prefix);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != NetworkAddress.AddressFamily)
            return false;
        return ApplyMask(address, PrefixLength).Equals(NetworkAddress);
    }

    public IReadOnlyList<IPAddress> SampleIPv4(int count)
    {
        if (NetworkAddress.AddressFamily != AddressFamily.InterNetwork || count <= 0)
            return Array.Empty<IPAddress>();

        var bytes = NetworkAddress.GetAddressBytes();
        var start = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var hostBits = 32 - PrefixLength;
        var size = hostBits == 32 ? (1UL << 32) : 1UL << hostBits;
        if (size <= 2)
            return new[] { NetworkAddress };

        var usable = size - 2;
        var result = new List<IPAddress>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = 1UL + ((ulong)(i + 1) * usable / (ulong)(count + 1));
            var value = (ulong)start + offset;
            result.Add(new IPAddress(new[]
            {
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            }));
        }
        return result;
    }

    private static IPAddress ApplyMask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var remaining = prefixLength - i * 8;
            bytes[i] = remaining switch
            {
                >= 8 => bytes[i],
                <= 0 => 0,
                _ => (byte)(bytes[i] & (0xFF << (8 - remaining)))
            };
        }
        return new IPAddress(bytes);
    }
}
