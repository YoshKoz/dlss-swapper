using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DLSS_Swapper.Data.DLSS5;

internal sealed class DLSS5AutopilotManager
{
    const string Repository = "Kizzuwatnaa/DLSS5-Autopilot";
    static readonly HttpClient HttpClient = CreateHttpClient();
    static readonly SemaphoreSlim InstallLock = new SemaphoreSlim(1, 1);

    static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS-Swapper-DLSS5-Integration");
        return client;
    }

    // Game folders can be huge and contain junctions or protected subfolders, so the probe must
    // not throw halfway through the walk and must not chase a junction into another drive.
    static readonly EnumerationOptions StatusSearchOptions = new EnumerationOptions()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 8,
    };

    public static Task<bool> IsInstalledAsync(string gamePath)
    {
        return Task.Run(() => IsInstalled(gamePath));
    }

    public static bool IsInstalled(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || Directory.Exists(gamePath) == false)
        {
            return false;
        }

        try
        {
            var statusPath = Directory.EnumerateFiles(gamePath, "dlss5-autopilot.json", StatusSearchOptions).FirstOrDefault();
            if (statusPath is null)
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(statusPath));
            return document.RootElement.TryGetProperty("complete", out var complete) && complete.GetBoolean();
        }
        catch (Exception err)
        {
            Logger.Warning($"Could not read DLSS5 status for {gamePath}: {err.Message}");
            return false;
        }
    }

    public async Task InstallAsync(string gamePath, string? route = null)
    {
        await RunAsync(gamePath, remove: false, route: route).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string gamePath)
    {
        await RunAsync(gamePath, remove: true, route: null).ConfigureAwait(false);
    }

    async Task RunAsync(string gamePath, bool remove, string? route)
    {
        if (Directory.Exists(gamePath) == false)
        {
            throw new DirectoryNotFoundException(gamePath);
        }

        await InstallLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var toolPath = await EnsureToolAsync().ConfigureAwait(false);
            var startInfo = new ProcessStartInfo(toolPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(gamePath);
            if (remove)
            {
                startInfo.ArgumentList.Add("--remove");
            }
            else if (string.IsNullOrEmpty(route) == false)
            {
                // Without --route the tool picks per game and GPU. Forcing optiscaler is wrong on
                // D3D11, where it replaces the game's DLSS with FSR.
                startInfo.ArgumentList.Add("--route");
                startInfo.ArgumentList.Add(route);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start DLSS5 Autopilot.");
            try
            {
                var standardOutputTask = process.StandardOutput.ReadToEndAsync();
                var standardErrorTask = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                var standardOutput = await standardOutputTask.ConfigureAwait(false);
                var standardError = await standardErrorTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
                    throw new InvalidOperationException($"DLSS5 Autopilot failed with exit code {process.ExitCode}: {details.Trim()}");
                }
            }
            catch (OperationCanceledException)
            {
                if (process.HasExited == false)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw new TimeoutException("DLSS5 Autopilot exceeded the ten-minute timeout.");
            }
        }
        finally
        {
            InstallLock.Release();
        }
    }

    static async Task<string> EnsureToolAsync()
    {
        using var releaseResponse = await HttpClient.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest").ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        using var releaseDocument = JsonDocument.Parse(await releaseResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        var assets = releaseDocument.RootElement.GetProperty("assets");
        string? zipUrl = null;
        string? sumsUrl = null;
        string? tag = releaseDocument.RootElement.GetProperty("tag_name").GetString();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = url;
            }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sumsUrl = url;
            }
        }

        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(zipUrl) || string.IsNullOrWhiteSpace(sumsUrl))
        {
            throw new InvalidOperationException("The latest DLSS5 Autopilot release did not contain a Windows archive and checksum file.");
        }

        var toolDirectory = Path.Combine(Storage.GetToolsPath(), "dlss5-autopilot", tag);
        var executablePath = Path.Combine(toolDirectory, "dlss5-autopilot.exe");
        if (File.Exists(executablePath))
        {
            return executablePath;
        }

        var archive = await HttpClient.GetByteArrayAsync(zipUrl).ConfigureAwait(false);
        var expectedSums = await HttpClient.GetStringAsync(sumsUrl).ConfigureAwait(false);
        var archiveHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var archiveName = Path.GetFileName(new Uri(zipUrl).AbsolutePath);
        var match = Regex.Match(expectedSums, $"(?im)^([0-9a-f]{{64}})\\s+\\*?{Regex.Escape(archiveName)}\\s*$");
        if (match.Success == false || string.Equals(match.Groups[1].Value, archiveHash, StringComparison.OrdinalIgnoreCase) == false)
        {
            throw new InvalidDataException("DLSS5 Autopilot checksum verification failed.");
        }

        var temporaryArchive = Path.Combine(Storage.GetTemp(), $"{tag}-dlss5-autopilot.zip");
        await File.WriteAllBytesAsync(temporaryArchive, archive).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(toolDirectory);
            ZipFile.ExtractToDirectory(temporaryArchive, toolDirectory, overwriteFiles: true);
        }
        finally
        {
            File.Delete(temporaryArchive);
        }

        if (File.Exists(executablePath) == false)
        {
            throw new FileNotFoundException("DLSS5 Autopilot archive did not contain its executable.", executablePath);
        }

        return executablePath;
    }
}
