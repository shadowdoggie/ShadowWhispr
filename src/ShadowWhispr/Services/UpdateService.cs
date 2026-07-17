using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShadowWhispr.Services;

/// <summary>Describes an available newer release and its installer asset.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string InstallerUrl,
    string? ChecksumUrl,
    string InstallerName,
    string Changelog);

/// <summary>What the user chose in the update confirmation prompt.</summary>
public enum UpdateChoice
{
    Decline,
    InstallNow,
    InstallOnClose,
}

/// <summary>
/// Keeps ShadowWhispr current without any manual download. On startup it asks
/// GitHub for the newest published release; if that release is newer than the
/// running build it downloads the installer to a temp folder, verifies it
/// against the release's published SHA-256, and hands back a path. The caller
/// runs that installer silently when the app closes (see MainWindow.OnClosing),
/// so files are never locked and the user is never interrupted mid-dictation.
///
/// Every step is logged to app-log.txt. Any failure is swallowed into a logged
/// warning — a failed update check must never break the app or block shutdown.
/// </summary>
public sealed partial class UpdateService
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/shadowdoggie/ShadowWhispr/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    public Version CurrentVersion { get; } =
        NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ShadowWhispr-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Returns the newest release if it is strictly newer than the running build,
    /// otherwise null. Never throws — network or parse failures log and return null.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AppLog.Write($"Update check: current version {CurrentVersion}");
            using var response = await Http.GetAsync(ReleasesApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Write($"Update check: GitHub returned {(int)response.StatusCode}; skipping");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !TryParseVersion(tag, out var latestVersion))
            {
                AppLog.Write($"Update check: could not parse release tag '{tag ?? "(none)"}'");
                return null;
            }

            if (latestVersion <= CurrentVersion)
            {
                AppLog.Write($"Update check: up to date (latest {latestVersion} <= current {CurrentVersion})");
                return null;
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                AppLog.Write("Update check: release has no assets array");
                return null;
            }

            string? installerUrl = null;
            string? installerName = null;
            string? checksumUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null)
                {
                    continue;
                }

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    installerUrl = url;
                    installerName = name;
                }
                else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }

            if (installerUrl is null || installerName is null)
            {
                AppLog.Write($"Update check: release {tag} has no .exe installer asset");
                return null;
            }

            var changelog = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                ? body.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            AppLog.Write($"Update available: {tag} (newer than {CurrentVersion})");
            return new UpdateInfo(latestVersion, tag, installerUrl, checksumUrl, installerName, changelog);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write("Update check failed", exception);
            return null;
        }
    }

    /// <summary>
    /// Downloads the installer to a temp folder and verifies it against the
    /// release's published SHA-256 when available. Returns the local path, or
    /// null if the download or verification failed (in which case nothing is run).
    /// </summary>
    public async Task<string?> DownloadInstallerAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ShadowWhispr", "updates");
        var installerPath = Path.Combine(directory, update.InstallerName);
        try
        {
            Directory.CreateDirectory(directory);
            CleanStaleInstallers(directory, update.InstallerName);

            AppLog.Write($"Downloading update {update.Tag} from {update.InstallerUrl}");
            await using (var source = await Http.GetStreamAsync(update.InstallerUrl, cancellationToken))
            await using (var destination = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            var expectedHash = await TryGetExpectedHashAsync(update, cancellationToken);
            if (expectedHash is not null)
            {
                var actualHash = await ComputeSha256Async(installerPath, cancellationToken);
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Write($"Update {update.Tag} REJECTED: checksum mismatch (expected {expectedHash}, got {actualHash})");
                    TryDelete(installerPath);
                    return null;
                }

                AppLog.Write($"Update {update.Tag} checksum verified");
            }
            else
            {
                AppLog.Write($"Update {update.Tag} has no published checksum; proceeding without verification");
            }

            AppLog.Write($"Update {update.Tag} downloaded to {installerPath}");
            return installerPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(installerPath);
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Downloading update {update.Tag} failed", exception);
            TryDelete(installerPath);
            return null;
        }
    }

    /// <summary>
    /// Launches the downloaded installer silently as the app exits, so the
    /// running executable no longer locks its own files. Used when the user
    /// chose "install when I close"; the app is not reopened. Inno Setup performs
    /// the in-place per-user upgrade (stable AppId) with no UAC prompt.
    /// </summary>
    public static bool InstallOnClose(string installerPath)
    {
        try
        {
            if (!File.Exists(installerPath))
            {
                AppLog.Write($"Update install skipped: installer missing at {installerPath}");
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            });
            AppLog.Write($"Update installer launched (install on close): {installerPath}");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write("Launching the update installer failed", exception);
            return false;
        }
    }

    /// <summary>
    /// Installs the update immediately and reopens the app. A detached helper
    /// waits for this process to exit (so its files unlock), runs the installer
    /// silently, then relaunches the app — after calling this, the caller shuts
    /// the app down. This avoids relying on the installer's Restart Manager.
    /// </summary>
    public static bool InstallNowAndRestart(string installerPath, string appExePath)
    {
        try
        {
            if (!File.Exists(installerPath))
            {
                AppLog.Write($"Update install skipped: installer missing at {installerPath}");
                return false;
            }

            var pid = Environment.ProcessId;
            var installer = EscapeForSingleQuoted(installerPath);
            var app = EscapeForSingleQuoted(appExePath);
            var command =
                "$ErrorActionPreference='SilentlyContinue';" +
                $"Wait-Process -Id {pid} -Timeout 60;" +
                $"Start-Process -FilePath '{installer}' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait;" +
                $"Start-Process -FilePath '{app}'";

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            AppLog.Write($"Update helper launched (install now + restart): {installerPath}");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write("Launching the update helper failed", exception);
            return false;
        }
    }

    private static string EscapeForSingleQuoted(string value) => value.Replace("'", "''");

    private async Task<string?> TryGetExpectedHashAsync(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.ChecksumUrl))
        {
            return null;
        }

        try
        {
            var content = await Http.GetStringAsync(update.ChecksumUrl, cancellationToken);
            // Accept both "<hash>" and "<hash>  filename" formats.
            var token = content.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is not null && HexHashRegex().IsMatch(token))
            {
                return token;
            }

            AppLog.Write("Update checksum file had an unexpected format; skipping verification");
            return null;
        }
        catch (Exception exception)
        {
            AppLog.Write("Fetching the update checksum failed; skipping verification", exception);
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void CleanStaleInstallers(string directory, string keepName)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.exe"))
            {
                if (!string.Equals(Path.GetFileName(file), keepName, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover temp installer is harmless; it is cleaned on the next run.
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var match = VersionRegex().Match(tag);
        if (match.Success)
        {
            version = new Version(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    // Compare on Major.Minor.Build only; installers stamp a 3-part version while
    // the assembly carries a 4-part one, so a raw compare would misfire.
    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    [GeneratedRegex(@"(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexHashRegex();
}
