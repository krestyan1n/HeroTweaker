using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class DeveloperDoctorService
{
    public static async Task<List<DevEnvironmentComponent>> ScanEnvironmentsAsync()
    {
        return await Task.Run(async () =>
        {
            var list = new List<DevEnvironmentComponent>();

            var checks = new (string Id, string Name, string Icon, string Exe, string Args)[]
            {
                ("git", "Git VCS", "🐙", "git.exe", "--version"),
                ("dotnet", ".NET SDK", "🟣", "dotnet.exe", "--version"),
                ("node", "Node.js", "🟢", "node.exe", "--version"),
                ("python", "Python", "🐍", "python.exe", "--version"),
                ("rust", "Rust (rustc)", "🦀", "rustc.exe", "--version"),
                ("go", "Golang", "🐹", "go.exe", "version"),
                ("docker", "Docker Daemon", "🐳", "docker.exe", "--version"),
                ("wsl", "WSL 2 (Linux)", "🐧", "wsl.exe", "--status"),
                ("vscode", "Visual Studio Code", "🟦", "code.cmd", "--version"),
            };

            foreach (var check in checks)
            {
                var comp = new DevEnvironmentComponent
                {
                    Id = check.Id,
                    Name = check.Name,
                    IconGlyph = check.Icon
                };

                string? fullPath = FindInPath(check.Exe);
                if (fullPath != null)
                {
                    comp.ExecutablePath = fullPath;
                    string output = await RunCliDirectAsync(fullPath, check.Args);

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        comp.Version = lines.Length > 0 ? lines[0].Trim() : output.Trim();
                        comp.Status = DevComponentStatus.Healthy;
                        comp.Details = $"Обнаружен исполняемый файл: {fullPath}";
                    }
                    else
                    {
                        comp.Version = "Ошибка ответа CLI";
                        comp.Status = DevComponentStatus.Warning;
                        comp.Details = "Файл найден в PATH, но процесс завершился без ответа.";
                    }
                }
                else
                {
                    comp.Status = DevComponentStatus.Missing;
                    comp.Version = "Не найден в PATH";
                    comp.Details = "Инструмент отсутствует или не прописан в системных переменных окружения.";
                }

                list.Add(comp);
            }

            return list;
        });
    }

    public static List<PathEntryItem> ScanPathEntries()
    {
        var result = new List<PathEntryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(userPath))
        {
            foreach (var raw in userPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string p = raw.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                bool exists = Directory.Exists(p);
                bool isDup = !seen.Add(p);
                result.Add(new PathEntryItem { Path = p, IsUserPath = true, ExistsOnDisk = exists, IsDuplicate = isDup });
            }
        }

        string? sysPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrEmpty(sysPath))
        {
            foreach (var raw in sysPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string p = raw.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                bool exists = Directory.Exists(p);
                bool isDup = !seen.Add(p);
                result.Add(new PathEntryItem { Path = p, IsUserPath = false, ExistsOnDisk = exists, IsDuplicate = isDup });
            }
        }

        return result;
    }

    public static async Task<int> CleanBrokenPathEntriesAsync()
    {
        return await Task.Run(() =>
        {
            int removedCount = 0;

            string? userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(userPath))
            {
                var cleanUserList = new List<string>();
                var seenUser = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in userPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = raw.Trim();
                    if (string.IsNullOrEmpty(p)) continue;

                    if (Directory.Exists(p) && seenUser.Add(p))
                    {
                        cleanUserList.Add(p);
                    }
                    else
                    {
                        removedCount++;
                    }
                }

                string newPath = string.Join(';', cleanUserList);
                Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.User);
            }

            string? sysPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrEmpty(sysPath))
            {
                var cleanSysList = new List<string>();
                var seenSys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in sysPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = raw.Trim();
                    if (string.IsNullOrEmpty(p)) continue;

                    if (Directory.Exists(p) && seenSys.Add(p))
                    {
                        cleanSysList.Add(p);
                    }
                    else
                    {
                        removedCount++;
                    }
                }

                string newSysPath = string.Join(';', cleanSysList);
                Environment.SetEnvironmentVariable("Path", newSysPath, EnvironmentVariableTarget.Machine);
            }

            return removedCount;
        });
    }

    public static async Task<CommandResolutionResult> ResolveCommandOwnerAsync(string commandName)
    {
        return await Task.Run(async () =>
        {
            var res = new CommandResolutionResult { Command = commandName };
            if (string.IsNullOrWhiteSpace(commandName))
            {
                res.Found = false;
                res.ResolvedExecutablePath = "Введите имя команды (например: python, git, node)";
                res.VersionOutput = "Пустой запрос";
                return res;
            }

            string cleanCmd = commandName.Trim().Replace("\"", "");
            var allMatches = FindAllInPath(cleanCmd);
            res.AllMatchingPaths = allMatches;

            if (allMatches.Count > 0)
            {
                res.Found = true;
                res.ResolvedExecutablePath = allMatches[0];
                res.VersionOutput = await RunCliDirectAsync(allMatches[0], "--version");
                if (string.IsNullOrWhiteSpace(res.VersionOutput))
                {
                    res.VersionOutput = await RunCliDirectAsync(allMatches[0], "-v");
                }
                if (string.IsNullOrWhiteSpace(res.VersionOutput))
                {
                    res.VersionOutput = "Файл найден в PATH, но не вернул стандартный ответ версии.";
                }
            }
            else
            {
                string? whereResult = await RunCliDirectAsync("where.exe", cleanCmd);
                if (!string.IsNullOrWhiteSpace(whereResult) && !whereResult.Contains("INFO:", StringComparison.OrdinalIgnoreCase))
                {
                    var lines = whereResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        res.Found = true;
                        res.ResolvedExecutablePath = lines[0].Trim();
                        res.AllMatchingPaths = lines.Select(l => l.Trim()).ToList();
                        res.VersionOutput = await RunCliDirectAsync(res.ResolvedExecutablePath, "--version");
                    }
                }

                if (!res.Found)
                {
                    res.Found = false;
                    res.ResolvedExecutablePath = $"Команда '{cleanCmd}' не найдена ни в одной из папок PATH";
                    res.VersionOutput = "Убедитесь, что утилита установлена и добавлена в системные переменные окружения.";
                }
            }

            return res;
        });
    }

    private static string? FindInPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        string[] exts = { "", ".exe", ".cmd", ".bat", ".com" };
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string cleanDir = dir.Trim().Trim('"');
                foreach (var ext in exts)
                {
                    string target = fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext;
                    string full = Path.Combine(cleanDir, target);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
        }
        return null;
    }

    private static List<string> FindAllInPath(string fileName)
    {
        var list = new List<string>();
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return list;

        string[] exts = { "", ".exe", ".cmd", ".bat", ".com" };
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string cleanDir = dir.Trim().Trim('"');
                foreach (var ext in exts)
                {
                    string target = fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext;
                    string full = Path.Combine(cleanDir, target);
                    if (File.Exists(full) && !list.Contains(full, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(full);
                    }
                }
            }
            catch { }
        }
        return list;
    }

    private static async Task<string> RunCliDirectAsync(string filePath, string args)
    {
        try
        {
            bool isScript = filePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                            filePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = isScript ? "cmd.exe" : filePath,
                Arguments = isScript ? $"/c call \"{filePath}\" {args}" : args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var readOut = proc.StandardOutput.ReadToEndAsync();
            var readErr = proc.StandardError.ReadToEndAsync();

            var timeoutTask = Task.Delay(3500);
            var completed = await Task.WhenAny(proc.WaitForExitAsync(), timeoutTask);

            if (completed != timeoutTask)
            {
                string outStr = (await readOut).Trim();
                string errStr = (await readErr).Trim();
                return !string.IsNullOrWhiteSpace(outStr) ? outStr : errStr;
            }
            else
            {
                try { proc.Kill(); } catch { }
                return string.Empty;
            }
        }
        catch { return string.Empty; }
    }
}