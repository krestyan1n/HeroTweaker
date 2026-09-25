using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            var connectTask = client.ConnectAsync(ipAddress, 53);
            var timeoutTask = Task.Delay(1000);

            if (await Task.WhenAny(connectTask, timeoutTask) == connectTask && client.Connected)
            {
                sw.Stop();
                return Math.Max(1, sw.ElapsedMilliseconds);
            }
        }
        catch { }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, 1000);
            if (reply.Status == IPStatus.Success)
            {
                return Math.Max(1, reply.RoundtripTime);
            }
        }
        catch { }

        return 9999;
    }

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
                    IpAddress = unicast.Address.ToString()
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

        return list;
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