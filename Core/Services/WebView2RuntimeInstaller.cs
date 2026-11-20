using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace HeroTweaker.Core.Services;

public static class WebView2RuntimeInstaller
{
    private static readonly Uri BootstrapperUri = new("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
    private const long MaxBootstrapperBytes = 20 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    public static bool TryGetInstalledVersion(out string version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            version = string.Empty;
            return false;
        }
        catch (COMException)
        {
            version = string.Empty;
            return false;
        }
    }

    public static async Task InstallEvergreenRuntimeAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker", "Prerequisites");
        Directory.CreateDirectory(installDirectory);
        var installerPath = Path.Combine(installDirectory, "MicrosoftEdgeWebView2Setup.exe");

        try
        {
            progress?.Report("Загрузка официального установщика Microsoft…");
            using var response = await Http.GetAsync(BootstrapperUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Установщик WebView2 должен загружаться только по защищённому HTTPS-соединению.");
            if (!string.IsNullOrEmpty(response.Content.Headers.ContentType?.MediaType) &&
                !string.Equals(response.Content.Headers.ContentType.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(response.Content.Headers.ContentType.MediaType, "application/x-msdownload", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Microsoft вернула неожиданный тип файла установщика.");
            }

            if (response.Content.Headers.ContentLength is > MaxBootstrapperBytes)
                throw new InvalidDataException("Размер установщика WebView2 превышает допустимый предел.");

            await using (var output = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxBootstrapperBytes)
                        throw new InvalidDataException("Размер установщика WebView2 превышает допустимый предел.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (!HasTrustedAuthenticodeSignature(installerPath))
                throw new InvalidDataException("Подпись установщика не прошла проверку Microsoft Authenticode.");

            progress?.Report("Установка Microsoft Edge WebView2 Runtime…");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/silent /install",
                UseShellExecute = true,
                WorkingDirectory = installDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Не удалось запустить установщик WebView2.");

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Установщик WebView2 завершился с кодом {process.ExitCode}.");

            if (!TryGetInstalledVersion(out _))
                throw new InvalidOperationException("Установщик завершился, но WebView2 Runtime пока не определяется. Перезагрузи компьютер и запусти HeroTweaker снова.");
        }
        finally
        {
            try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        }
    }

    private static bool HasTrustedAuthenticodeSignature(string path)
    {
        try
        {
            // WinVerifyTrust validates the PE Authenticode chain below; this legacy API is
            // used only to inspect the signer's organization for the expected Microsoft publisher.
#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            if (!signer.Subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                return false;

            var fileInfo = new WinTrustFileInfo(path);
            var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                var data = new WinTrustData(fileInfoPointer);
                var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }

        }
        catch
        {
            return false;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 0; // WTD_REVOKE_NONE; validate the embedded chain offline.
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = 0; // WTD_STATEACTION_IGNORE
            StateData = IntPtr.Zero;
            UrlReference = null;
            ProviderFlags = 0x00001000; // WTD_CACHE_ONLY_URL_RETRIEVAL
            UiContext = 0;
        }
    }
}
