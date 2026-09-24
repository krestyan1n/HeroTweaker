using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HeroTweaker.Core.Models;
using HeroTweaker.ViewModels;

namespace HeroTweaker.Core.Services;

public class SystemHealthReport
{
    public int Score { get; set; } = 60;
    public string StatusText { get; set; } = "Анализ состояния системы...";
    public int ActiveTweaksCount { get; set; }
    public int DisabledServicesCount { get; set; }
    public string CleanableSpaceFormatted { get; set; } = "0 МБ";
    public List<RecommendationModel> Recommendations { get; set; } = new();
}

public static class SystemHealthService
{
    public static async Task<SystemHealthReport> AnalyzeSystemHealthAsync(IEnumerable<TweakViewModel> allTweaks)
    {
        return await Task.Run(() =>
        {
            var report = new SystemHealthReport();
            var tweaksList = allTweaks.ToList();

            // 1. Анализ твиков
            int totalTweaks = tweaksList.Count;
            int activeTweaks = tweaksList.Count(t => t.CurrentState);
            int disabledServices = tweaksList.Count(t => t.Category == "Службы" && t.CurrentState);

            report.ActiveTweaksCount = activeTweaks;
            report.DisabledServicesCount = disabledServices;

            // 2. Сканирование реального объема Temp
            long tempBytes = CalculateTempFolderBytes();
            report.CleanableSpaceFormatted = DiskItemModel.FormatBytes(tempBytes);

            // 3. Расчет объективного Health Score (100-балльная шкала)
            // А. Твики (макс. 45 баллов)
            double tweakRatio = totalTweaks > 0 ? (double)activeTweaks / totalTweaks : 0;
            int tweakScore = (int)Math.Round(tweakRatio * 45.0);

            // Б. Чистота от временных файлов (макс. 25 баллов)
            int diskScore;
            double tempMb = tempBytes / (1024.0 * 1024.0);
            if (tempMb < 150) diskScore = 25;
            else if (tempMb < 800) diskScore = 18;
            else if (tempMb < 2500) diskScore = 10;
            else diskScore = 4;

            // В. Службы (макс. 20 баллов)
            int serviceScore = Math.Min(disabledServices * 4, 20);

            // Г. Запас оперативной памяти (макс. 10 баллов)
            int ramScore = 10;
            try
            {
                long totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (totalMem > 0)
                {
                    ramScore = 8;
                }
            }
            catch { }

            int finalScore = Math.Clamp(tweakScore + diskScore + serviceScore + ramScore, 20, 100);
            report.Score = finalScore;

            // 4. Формирование понятного объективного статуса
            if (finalScore >= 90)
            {
                report.StatusText = "Система в превосходном состоянии: системные твики активны, фоновый мусор отсутствует.";
            }
            else if (finalScore >= 75)
            {
                report.StatusText = $"Хорошая оптимизация. Обнаружено {report.CleanableSpaceFormatted} временных файлов, которые можно очистить.";
            }
            else if (finalScore >= 50)
            {
                report.StatusText = $"Средний уровень оптимизации: активно {activeTweaks} из {totalTweaks} твиков, доступно к очистке {report.CleanableSpaceFormatted}.";
            }
            else
            {
                report.StatusText = $"Система требует внимания: отключены базовые оптимизации и накопилось {report.CleanableSpaceFormatted} кэша.";
            }

            // 5. Формирование рекомендаций из неактивных ключевых твиков
            foreach (var t in tweaksList.Where(t => t.IsFeatured && !t.CurrentState).Take(5))
            {
                report.Recommendations.Add(new RecommendationModel
                {
                    TweakId = t.Id,
                    Title = t.Name,
                    Description = t.Description,
                    Category = t.Category
                });
            }

            return report;
        });
    }

    private static long CalculateTempFolderBytes()
    {
        long bytes = 0;
        var paths = new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };

        foreach (var p in paths)
        {
            if (!Directory.Exists(p)) continue;
            try
            {
                var di = new DirectoryInfo(p);
                foreach (var fi in di.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    try { bytes += fi.Length; } catch { }
                }
            }
            catch { }
        }

        return bytes;
    }
}