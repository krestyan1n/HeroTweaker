using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class NetworkOptimizerService
{
    public static List<DnsServerItem> GetPredefinedDnsList()
    {
        return new List<DnsServerItem>
        {
            new()
            {
                Id = "cloudflare",
                Name = "Cloudflare DNS",
                PrimaryIp = "1.1.1.1",
                SecondaryIp = "1.0.0.1",
                Description = "Максимальная скорость отклика, приватность и защита от логирования.",
                IconGlyph = "⚡"
            },
            new()
            {
                Id = "google",
                Name = "Google Public DNS",
                PrimaryIp = "8.8.8.8",
                SecondaryIp = "8.8.4.4",
                Description = "Всемирно известная глобальная сеть серверов с высокой доступностью.",
                IconGlyph = "🌍"
            },
            new()
            {
                Id = "quad9",
                Name = "Quad9 Security",
                PrimaryIp = "9.9.9.9",
                SecondaryIp = "149.112.112.112",
                Description = "Автоматическая блокировка фишинговых сайтов, вредоносного ПО и ботнетов.",
                IconGlyph = "🛡️"
            },
            new()
            {
                Id = "adguard",
                Name = "AdGuard DNS",
                PrimaryIp = "94.140.14.14",
                SecondaryIp = "94.140.15.15",
                Description = "Фильтрация рекламы, баннеров и счетчиков трекинга на уровне DNS-запросов.",
                IconGlyph = "🚫"
            },
            new()
            {
                Id = "yandex",
                Name = "Яндекс DNS",
                PrimaryIp = "77.88.8.8",
                SecondaryIp = "77.88.8.1",
                Description = "Минимальные задержки и быстрые маршруты на территории России и СНГ.",
                IconGlyph = "🔴"
            }
        };
    }

    public static async Task MeasureDnsLatencyAsync(IEnumerable<DnsServerItem> dnsList)
    {
        var tasks = dnsList.Select(async item =>
        {
            long latency = await MeasureSingleDnsLatencyAsync(item.PrimaryIp);

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                item.PingMs = latency;
                item.IsFastest = false;
            });
        });

        await Task.WhenAll(tasks);

        Application.Current?.Dispatcher?.Invoke(() =>
        {
            var valid = dnsList.Where(d => d.PingMs > 0 && d.PingMs < 9000).ToList();
            if (valid.Count > 0)
            {
                long min = valid.Min(d => d.PingMs);
                foreach (var d in dnsList)
                {
                    d.IsFastest = (d.PingMs == min);
                }
            }
        });
    }

    private static async Task<long> MeasureSingleDnsLatencyAsync(string ipAddress)
    {
        var udpLatency = await MeasureDnsUdpLatencyAsync(ipAddress);
        if (udpLatency > 0) return udpLatency;

        var tcpLatency = await MeasureDnsTcpLatencyAsync(ipAddress);
        if (tcpLatency > 0) return tcpLatency;

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, 1_200);
            if (reply.Status == IPStatus.Success)
            {
                return Math.Max(1, reply.RoundtripTime);
            }
        }
        catch { }

        return 9999;
    }

    private static async Task<long> MeasureDnsUdpLatencyAsync(string ipAddress)
    {
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Connect(ipAddress, 53);

            var query = CreateDnsQuery();
            var stopwatch = Stopwatch.StartNew();
            await client.SendAsync(query, query.Length);

            var receiveTask = client.ReceiveAsync();
            if (await Task.WhenAny(receiveTask, Task.Delay(1_500)) != receiveTask)
                return 0;

            var response = await receiveTask;
            stopwatch.Stop();

            return response.Buffer.Length >= 12 ? Math.Max(1, stopwatch.ElapsedMilliseconds) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<long> MeasureDnsTcpLatencyAsync(string ipAddress)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var stopwatch = Stopwatch.StartNew();
            var connectTask = client.ConnectAsync(ipAddress, 53);

            if (await Task.WhenAny(connectTask, Task.Delay(1_200)) != connectTask || !client.Connected)
                return 0;

            stopwatch.Stop();
            return Math.Max(1, stopwatch.ElapsedMilliseconds);
        }
        catch
        {
            return 0;
        }
    }

    private static byte[] CreateDnsQuery()
    {
        var transactionId = unchecked((ushort)Interlocked.Increment(ref _dnsTransactionId));
        return new byte[]
        {
            (byte)(transactionId >> 8), (byte)transactionId,
            0x01, 0x00, // standard recursive query
            0x00, 0x01, // one question
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            0x03, (byte)'c', (byte)'o', (byte)'m', 0x00,
            0x00, 0x01, // A record
            0x00, 0x01  // IN class
        };
    }

    private static int _dnsTransactionId;

    public static List<NetworkAdapterInfo> GetAllPhysicalAdapters()
    {
        var list = new List<NetworkAdapterInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic =>
                    nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                    nic.OperationalStatus == OperationalStatus.Up && // ТОЛЬКО РАБОТАЮЩИЕ И ВКЛЮЧЕННЫЕ
                    !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) &&
                    !nic.Name.Contains("Pseudo", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .ThenBy(nic => nic.Name);

            foreach (var nic in interfaces)
            {
                var ipProp = nic.GetIPProperties();

                // Пропускаем адаптеры без назначенного IP (мерхие/отключенные интерфейсы)
                var unicast = ipProp.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (unicast == null) continue;

                var adapter = new NetworkAdapterInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    IsUp = true,
                    InterfaceType = nic.NetworkInterfaceType,
                    IpAddress = unicast.Address.ToString(),
                    IsDefaultRoute = ipProp.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !System.Net.IPAddress.Any.Equals(gateway.Address))
                };

                var dnsList = ipProp.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                if (dnsList.Count > 0)
                {
                    adapter.CurrentDns = string.Join(", ", dnsList.Select(a => a.ToString()));
                }
                else
                {
                    adapter.CurrentDns = "DHCP (Автоматически)";
                }

                list.Add(adapter);
            }
        }
        catch { }

        return list
            .OrderByDescending(adapter => adapter.IsDefaultRoute)
            .ThenByDescending(adapter => adapter.InterfaceType == NetworkInterfaceType.Ethernet)
            .ThenBy(adapter => adapter.Name)
            .ToList();
    }

    public static async Task ApplyDnsAsync(string adapterName, string primaryDns, string secondaryDns)
    {
        await Task.Run(() =>
        {
            try
            {
                string cmdPrimary = $"netsh interface ipv4 set dnsservers \"{adapterName}\" static {primaryDns} primary";
                RunProcess("cmd.exe", $"/c {cmdPrimary}");

                if (!string.IsNullOrEmpty(secondaryDns))
                {
                    string cmdSec = $"netsh interface ipv4 add dnsservers \"{adapterName}\" {secondaryDns} index=2";
                    RunProcess("cmd.exe", $"/c {cmdSec}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Не удалось назначить DNS на адаптер {adapterName}: {ex.Message}");
            }
        });
    }

    public static async Task ResetDnsToDhcpAsync(string adapterName)
    {
        await Task.Run(() =>
        {
            try
            {
                RunProcess("cmd.exe", $"/c netsh interface ipv4 set dnsservers \"{adapterName}\" dhcp");
            }
            catch (Exception ex)
            {
                throw new Exception($"Не удалось сбросить DNS: {ex.Message}");
            }
        });
    }

    public static async Task FlushDnsCacheAsync()
    {
        await Task.Run(() =>
        {
            RunProcess("ipconfig.exe", "/flushdns");
        });
    }

    public static async Task ResetWinsockAndTcpAsync()
    {
        await Task.Run(() =>
        {
            RunProcess("netsh.exe", "winsock reset");
            RunProcess("netsh.exe", "int ip reset");
        });
    }

    public static async Task OptimizeNagleAlgorithmAsync(string? adapterGuid, bool enableLowLatency)
    {
        await Task.Run(() =>
        {
            try
            {
                const string interfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var root = baseKey.OpenSubKey(interfacesPath, writable: true);
                if (root == null) return;

                var targetKeys = new List<string>();

                if (!string.IsNullOrEmpty(adapterGuid) && root.GetSubKeyNames().Contains(adapterGuid, StringComparer.OrdinalIgnoreCase))
                {
                    targetKeys.Add(adapterGuid);
                }
                else
                {
                    targetKeys.AddRange(root.GetSubKeyNames());
                }

                foreach (var subKeyName in targetKeys)
                {
                    using var subKey = root.OpenSubKey(subKeyName, writable: true);
                    if (subKey == null) continue;

                    if (enableLowLatency)
                    {
                        subKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                        subKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                        subKey.SetValue("TcpDelAckTicks", 0, RegistryValueKind.DWord);
                    }
                    else
                    {
                        subKey.DeleteValue("TcpAckFrequency", false);
                        subKey.DeleteValue("TCPNoDelay", false);
                        subKey.DeleteValue("TcpDelAckTicks", false);
                    }
                }
            }
            catch { }
        });
    }

    public static bool IsNagleOptimized(string? adapterGuid)
    {
        try
        {
            const string interfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(interfacesPath);
            if (root == null) return false;

            if (!string.IsNullOrEmpty(adapterGuid))
            {
                using var target = root.OpenSubKey(adapterGuid);
                if (target?.GetValue("TcpAckFrequency") is int ack && ack == 1 &&
                    target?.GetValue("TCPNoDelay") is int noDelay && noDelay == 1)
                {
                    return true;
                }
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey?.GetValue("TcpAckFrequency") is int ack && ack == 1 &&
                    subKey?.GetValue("TCPNoDelay") is int noDelay && noDelay == 1)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static void RunProcess(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(4000);
    }
}
