using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class AppManagerService
{
    public static List<CatalogAppModel> GetCatalogApps()
    {
        return new List<CatalogAppModel>
        {
            // === БРАУЗЕРЫ ===
            new()
            {
                WingetId = "Google.Chrome",
                Name = "Google Chrome",
                Category = "Браузеры",
                Description = "Быстрый и надежный веб-браузер от компании Google.",
                IconGlyph = "🌐",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/google-chrome.png"
            },
            new()
            {
                WingetId = "Mozilla.Firefox",
                Name = "Mozilla Firefox",
                Category = "Браузеры",
                Description = "Свободный браузер с усиленной защитой конфиденциальности.",
                IconGlyph = "🦊",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/firefox.png"
            },
            new()
            {
                WingetId = "Brave.Brave",
                Name = "Brave Browser",
                Category = "Браузеры",
                Description = "Браузер со встроенной блокировкой рекламы и трекеров слежения.",
                IconGlyph = "🦁",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/brave.png"
            },
            new()
            {
                WingetId = "Yandex.Browser",
                Name = "Яндекс Браузер",
                Category = "Браузеры",
                Description = "Браузер с нейросетевыми функциями, переводом видео и защитой.",
                IconGlyph = "🔴",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/yandex.png"
            },

            // === РАЗРАБОТКА & IT ===
            new()
            {
                WingetId = "Microsoft.VisualStudioCode",
                Name = "Visual Studio Code",
                Category = "Разработка",
                Description = "Легковесный и мощный редактор исходного кода от Microsoft.",
                IconGlyph = "🟦",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/visual-studio-code.png"
            },
            new()
            {
                WingetId = "Git.Git",
                Name = "Git",
                Category = "Разработка",
                Description = "Быстрая распределенная система управления версиями.",
                IconGlyph = "🐙",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/git.png"
            },
            new()
            {
                WingetId = "OpenJS.NodeJS",
                Name = "Node.js",
                Category = "Разработка",
                Description = "Кроссплатформенная среда исполнения JavaScript (V8).",
                IconGlyph = "🟢",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/nodejs.png"
            },
            new()
            {
                WingetId = "Python.Python.3.12",
                Name = "Python 3.12",
                Category = "Разработка",
                Description = "Официальный дистрибутив и интерпретатор языка Python.",
                IconGlyph = "🐍",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/python.png"
            },
            new()
            {
                WingetId = "Docker.DockerDesktop",
                Name = "Docker Desktop",
                Category = "Разработка",
                Description = "Универсальная среда контейнеризации приложений.",
                IconGlyph = "🐳",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/docker.png"
            },
            new()
            {
                WingetId = "Postman.Postman",
                Name = "Postman",
                Category = "Разработка",
                Description = "Инструмент для проектирования, тестирования и отладки REST API.",
                IconGlyph = "🚀",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/postman.png"
            },
            new()
            {
                WingetId = "dbeaver.dbeaver",
                Name = "DBeaver",
                Category = "Разработка",
                Description = "Универсальный GUI-клиент баз данных для разработчиков и DBA.",
                IconGlyph = "🗄️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/dbeaver.png"
            },

            // === УТИЛИТЫ ===
            new()
            {
                WingetId = "7zip.7zip",
                Name = "7-Zip",
                Category = "Утилиты",
                Description = "Архиватор с открытым исходным кодом и максимальным сжатием 7z.",
                IconGlyph = "🗜️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/7zip.png"
            },
            new()
            {
                WingetId = "Rufus.Rufus",
                Name = "Rufus",
                Category = "Утилиты",
                Description = "Создание загрузочных USB-дисков и образов Windows/Linux.",
                IconGlyph = "💾",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/rufus.png"
            },
            new()
            {
                WingetId = "Notepad++.Notepad++",
                Name = "Notepad++",
                Category = "Утилиты",
                Description = "Быстрый редактор текстовых файлов и исходного кода.",
                IconGlyph = "📝",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/notepad-plus-plus.png"
            },
            new()
            {
                WingetId = "voidtools.Everything",
                Name = "Everything",
                Category = "Утилиты",
                Description = "Мгновенный локальный поиск файлов и папок по файловой системе NTFS.",
                IconGlyph = "🔍",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/everything.png"
            },
            new()
            {
                WingetId = "Microsoft.PowerToys",
                Name = "PowerToys",
                Category = "Утилиты",
                Description = "Системный комплекс надстроек для Windows от Microsoft.",
                IconGlyph = "🛠️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/powertoys.png"
            },
            new()
            {
                WingetId = "CPUID.CPU-Z",
                Name = "CPU-Z",
                Category = "Утилиты",
                Description = "Подробная информация о процессоре, оперативной памяти и плате.",
                IconGlyph = "🖥️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/cpu-z.png"
            },
            new()
            {
                WingetId = "TechPowerUp.GPU-Z",
                Name = "GPU-Z",
                Category = "Утилиты",
                Description = "Диагностика и аппаратный мониторинг видеокарты.",
                IconGlyph = "🎛️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/gpu-z.png"
            },

            // === ОБЩЕНИЕ & МЕДИА ===
            new()
            {
                WingetId = "Telegram.TelegramDesktop",
                Name = "Telegram",
                Category = "Общение",
                Description = "Защищенный и скоростной мессенджер с облачной синхронизацией.",
                IconGlyph = "✈️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/telegram.png"
            },
            new()
            {
                WingetId = "Discord.Discord",
                Name = "Discord",
                Category = "Общение",
                Description = "Голосовой и текстовый чат для сообществ и геймеров.",
                IconGlyph = "💬",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/discord.png"
            },
            new()
            {
                WingetId = "VideoLAN.VLC",
                Name = "VLC Media Player",
                Category = "Медиа",
                Description = "Медиаплеер со встроенными кодеками для всех видеоформатов.",
                IconGlyph = "▶️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/vlc.png"
            },
            new()
            {
                WingetId = "OBSProject.OBSStudio",
                Name = "OBS Studio",
                Category = "Медиа",
                Description = "Профессиональная программа для захвата видео и стриминга.",
                IconGlyph = "🎥",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/obs-studio.png"
            },
            new()
            {
                WingetId = "Spotify.Spotify",
                Name = "Spotify",
                Category = "Медиа",
                Description = "Стриминговый сервис музыки, подкастов и персональных рекомендаций.",
                IconGlyph = "🎵",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/spotify.png"
            },

            // === ИГРЫ ===
            new()
            {
                WingetId = "Valve.Steam",
                Name = "Steam",
                Category = "Игры",
                Description = "Главная цифровая игровая платформа с облачными сохранениями.",
                IconGlyph = "🎮",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/steam.png"
            },
            new()
            {
                WingetId = "EpicGames.EpicGamesLauncher",
                Name = "Epic Games Launcher",
                Category = "Игры",
                Description = "Магазин игр и лаунчер для Fortnite, Unreal Engine и раздач.",
                IconGlyph = "🕹️",
                IconUrl = "https://raw.githubusercontent.com/walkxcode/dashboard-icons/main/png/epic-games.png"
            }
        };
    }

    public static async Task InstallAppViaWingetAsync(string wingetId, Action<string, LogLevel> logCallback)
    {
        await Task.Run(() =>
        {
            try
            {
                logCallback?.Invoke($"[Winget] Запуск установки пакета {wingetId}...", LogLevel.Command);
                using var proc = new Process();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"install --id {wingetId} --exact --accept-package-agreements --accept-source-agreements --silent",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logCallback?.Invoke(e.Data, LogLevel.Info); };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logCallback?.Invoke(e.Data, LogLevel.Error); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                if (proc.ExitCode == 0)
                    logCallback?.Invoke($"[Winget] Пакет {wingetId} успешно установлен.", LogLevel.Success);
                else
                    logCallback?.Invoke($"[Winget] Установка {wingetId} завершена с кодом {proc.ExitCode}.", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[Winget] Ошибка установки: {ex.Message}", LogLevel.Error);
                throw;
            }
        });
    }

    public static async Task<List<InstalledProgramModel>> GetInstalledProgramsAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<InstalledProgramModel>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var registryPaths = new[]
            {
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
            };

            foreach (var (hive, path) in registryPaths)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = baseKey.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        string displayName = subKey.GetValue("DisplayName") as string ?? string.Empty;
                        string uninstallString = subKey.GetValue("UninstallString") as string ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(uninstallString))
                        {
                            if (displayName.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                                displayName.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (!seenKeys.Add(displayName)) continue;

                            string version = subKey.GetValue("DisplayVersion") as string ?? "Неизвестно";
                            string publisher = subKey.GetValue("Publisher") as string ?? "Неизвестный издатель";

                            string sizeFormatted = "";
                            if (subKey.GetValue("EstimatedSize") is int sizeKb)
                            {
                                sizeFormatted = $"{(sizeKb / 1024.0):F1} МБ";
                            }

                            list.Add(new InstalledProgramModel
                            {
                                DisplayName = displayName,
                                DisplayVersion = version,
                                Publisher = publisher,
                                UninstallString = uninstallString,
                                EstimatedSize = sizeFormatted
                            });
                        }
                    }
                }
                catch { }
            }

            return list.OrderBy(p => p.DisplayName).ToList();
        });
    }

    public static void RunUninstall(string uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return;

        try
        {
            string fileName = uninstallString;
            string args = "";

            if (uninstallString.StartsWith("\""))
            {
                int quoteIndex = uninstallString.IndexOf("\"", 1);
                if (quoteIndex != -1)
                {
                    fileName = uninstallString.Substring(1, quoteIndex - 1);
                    args = uninstallString.Substring(quoteIndex + 1).Trim();
                }
            }
            else
            {
                int exeIndex = uninstallString.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIndex != -1)
                {
                    fileName = uninstallString.Substring(0, exeIndex + 4);
                    args = uninstallString.Substring(exeIndex + 4).Trim();
                }
                else
                {
                    int spaceIndex = uninstallString.IndexOf(" ");
                    if (spaceIndex != -1)
                    {
                        fileName = uninstallString.Substring(0, spaceIndex);
                        args = uninstallString.Substring(spaceIndex + 1);
                    }
                }
            }

            if (fileName.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                fileName = "msiexec.exe";
                args = uninstallString.Substring(uninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) + 7).Trim();
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            throw new Exception($"Не удалось запустить деинсталлятор.\nОшибка: {ex.Message}");
        }
    }

    public static async Task RestoreMicrosoftStoreAsync(Action<string, LogLevel> logCallback)
    {
        await Task.Run(() =>
        {
            try
            {
                logCallback?.Invoke("Начало восстановления Microsoft Store через PowerShell...", LogLevel.Command);
                using var proc = new Process();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-AppxPackage -allusers Microsoft.WindowsStore | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register '$($_.InstallLocation)\\AppXManifest.xml'}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logCallback?.Invoke(e.Data, LogLevel.Info); };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logCallback?.Invoke(e.Data, LogLevel.Error); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                logCallback?.Invoke("Команда восстановления Store завершена.", LogLevel.Success);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"Ошибка восстановления: {ex.Message}", LogLevel.Error);
            }
        });
    }

    public static async Task RestoreAllDefaultUwpAppsAsync(Action<string, LogLevel> logCallback)
    {
        await Task.Run(() =>
        {
            try
            {
                logCallback?.Invoke("Начало перерегистрации всех стандартных UWP-приложений...", LogLevel.Command);
                using var proc = new Process();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-AppXPackage -AllUsers | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register '$($_.InstallLocation)\\AppXManifest.xml'}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logCallback?.Invoke(e.Data, LogLevel.Info); };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logCallback?.Invoke(e.Data, LogLevel.Error); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                logCallback?.Invoke("Перерегистрация UWP приложений завершена.", LogLevel.Success);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"Ошибка перерегистрации: {ex.Message}", LogLevel.Error);
            }
        });
    }
}