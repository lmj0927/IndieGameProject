using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Poker.Transport
{
    public sealed class HoldemLanAddress
    {
        internal HoldemLanAddress(string address, string adapter) { Address = address; Adapter = adapter; }
        public string Address { get; }
        public string Adapter { get; }
        public string Label => Address + " · " + Adapter;
    }

    /// <summary>Local interface hints only. No probes, DNS, socket creation or reachability promises.</summary>
    public static class HoldemLanAddresses
    {
        public static HoldemLanAddress[] ReadLocal()
        {
            var result = new List<HoldemLanAddress>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                        {
                            if (!IsCandidate(entry.Address, adapter.OperationalStatus, adapter.NetworkInterfaceType)) continue;
                            string address = entry.Address.ToString();
                            if (seen.Add(address)) result.Add(new HoldemLanAddress(address, CleanName(adapter.Name)));
                        }
                    }
                    catch (NetworkInformationException) { } // An adapter can disappear while refreshing the list.
                }
            }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }
            result.Sort((a, b) => string.CompareOrdinal(a.Address, b.Address));
            return result.ToArray();
        }

        public static bool IsCandidate(IPAddress address, OperationalStatus status, NetworkInterfaceType type)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork || status != OperationalStatus.Up
                || type == NetworkInterfaceType.Loopback || type == NetworkInterfaceType.Tunnel) return false;
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        }

        internal static bool IsAssignedLocally(IPAddress address, Func<NetworkInterface[]> readInterfaces)
        {
            try
            {
                foreach (var adapter in readInterfaces())
                {
                    try
                    {
                        foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                            if (unicast.Address.Equals(address)) return true;
                    }
                    catch (NetworkInformationException) { }
                    catch (NotSupportedException) { }
                }
            }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }
            return false;
        }

        private static string CleanName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "네트워크";
            var result = new System.Text.StringBuilder();
            foreach (char c in value) if (!char.IsControl(c) && c != '<' && c != '>' && result.Length < 28) result.Append(c);
            return result.Length == 0 ? "네트워크" : result.ToString();
        }
    }
}
