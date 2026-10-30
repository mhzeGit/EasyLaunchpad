using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyTools.Updating;

// Kept identical in EasySearch and EasyLaunchpad. No credentials or Git installation required.
public sealed record UpdateRelease(string Version, long AssetId, string Digest, string Url, long Size, DateTimeOffset UpdatedAt, string? BuildId = null);

public sealed class AppUpdater
{
    public const string ManifestName = "update-manifest.json";
    const string ReceiptName = ".easy-update-receipt.json";
    const string PendingName = ".easy-update-pending.json";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly HttpClient Http = CreateHttp();
    readonly string repository, product, executable, installDirectory;
    readonly string currentVersion;

    public AppUpdater(string repository, string product, string executable)
    {
        this.repository = repository;
        this.product = product;
        this.executable = executable;
        installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        currentVersion = Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3);
    }

    static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyTools-Updater/1.0");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellation = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new IOException("No public GitHub release is available. Check that the repository and release are public.");
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            throw new IOException("GitHub's request limit has been reached. Try again later.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var release = ParseRelease(document.RootElement, product, repository);
        if (release is null) throw new IOException("The latest release has no compatible Windows x64 update with a SHA-256 checksum.");
        Receipt? receipt = null;
        Manifest? manifest = null;
        try
        {
            if (File.Exists(Path.Combine(installDirectory, ReceiptName)))
                receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(Path.Combine(installDirectory, ReceiptName)), Json);
            if (File.Exists(Path.Combine(installDirectory, ManifestName)))
                manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(installDirectory, ManifestName)), Json);
        }
        catch (JsonException) { /* Missing installation metadata must not prevent a repair. */ }
        return IsNewer(release, currentVersion, receipt?.AssetId, receipt?.Digest,
            manifest?.BuiltUtc ?? File.GetLastWriteTimeUtc(Path.Combine(installDirectory, executable)), manifest?.BuildId) ? release : null;
    }

    public static bool IsNewer(UpdateRelease release, string current, long? installedAsset, string? installedDigest, DateTimeOffset builtUtc, string? currentBuildId = null)
    {
        int comparison = ParseVersion(release.Version).CompareTo(ParseVersion(current));
        if (comparison < 0) return false;
        if (comparison > 0) return true;
        if (release.BuildId is { } buildId && string.Equals(currentBuildId, buildId, StringComparison.OrdinalIgnoreCase)) return false;
        // Releases in these repositories are replaced without necessarily changing the version.
        if (installedAsset.HasValue)
            return installedAsset != release.AssetId || !string.Equals(installedDigest, release.Digest, StringComparison.OrdinalIgnoreCase);
        return release.UpdatedAt > builtUtc;
    }

    static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^v?\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The release version is not a stable numeric version.");
        var version = Version.Parse(value.TrimStart('v'));
        return new Version(version.Major, version.Minor, version.Build, Math.Max(version.Revision, 0));
    }

    public static UpdateRelease? ParseRelease(JsonElement root, string product, string repository)
    {
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string version = root.GetProperty("tag_name").GetString()!;
        ParseVersion(version);
        string name = $"{product}-{version.TrimStart('v')}-win-x64.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name || asset.GetProperty("state").GetString() != "uploaded") continue;
            string digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
            string url = asset.GetProperty("browser_download_url").GetString()!;
            if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("The update download does not belong to this GitHub repository.");
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > 512L * 1024 * 1024) throw new InvalidDataException("The update package size is invalid.");
            string label = asset.TryGetProperty("label", out var labelValue) ? labelValue.GetString() ?? "" : "";
            string? buildId = Regex.IsMatch(label, "^build:[a-fA-F0-9]{64}$") ? label[6..] : null;
            return new UpdateRelease(version.TrimStart('v'), asset.GetProperty("id").GetInt64(), digest, url, size,
                asset.GetProperty("updated_at").GetDateTimeOffset(), buildId);
        }
        return null;
    }

    // Returns only after the detached installer has acquired its lock and is ready for this app to exit.
    public async Task InstallAsync(UpdateRelease release, IProgress<string> progress, CancellationToken cancellation = default)
    {
        if (File.Exists(Path.Combine(installDirectory, PendingName))) throw new IOException("Another update is already running. Restart the app to recover an interrupted update.");
        string probe = Path.Combine(installDirectory, $".easy-update-write-{Guid.NewGuid():N}");
        try { File.WriteAllText(probe, ""); }
        catch (UnauthorizedAccessException) { throw new IOException("This app folder is read-only. Move the portable app to a writable folder and try again."); }
        finally { if (File.Exists(probe)) File.Delete(probe); }

        string work = Path.Combine(Path.GetTempPath(), $"{product}-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        bool handedOff = false;
        Process? worker = null;
        try
        {
            progress.Report("Downloading update…");
            string archive = Path.Combine(work, "update.zip");
            using (var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                using var input = await response.Content.ReadAsStreamAsync(cancellation);
                using var output = File.Create(archive);
                var buffer = new byte[81920];
                long total = 0;
                int lastPercent = -1, read;
                while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("The download size does not match GitHub.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    int percent = (int)(total * 100 / release.Size);
                    if (percent != lastPercent) { progress.Report($"Downloading update… {percent}%"); lastPercent = percent; }
                }
                if (total != release.Size) throw new InvalidDataException("The update download was incomplete.");
            }
            progress.Report("Verifying update…");
            using (var stream = File.OpenRead(archive))
            {
                string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
                if (!string.Equals(release.Digest, "sha256:" + digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The update checksum failed. No installed files were changed.");
            }
            string stage = Path.Combine(work, "stage");
            await Task.Run(() => ExtractPackage(archive, stage, product, executable, release.Version), cancellation);
            File.Delete(archive);
            var oldManifest = Path.Combine(installDirectory, ManifestName);
            if (!File.Exists(oldManifest)) throw new IOException("This build has no file inventory. Install the current release once before using automatic updates.");
            ValidateManifest(installDirectory, product, executable, verifyHashes: false);
            string helper = Path.Combine(work, "apply.ps1");
            using (var resource = typeof(AppUpdater).Assembly.GetManifestResourceStream("EasyTools.ApplyUpdate.ps1")!)
            using (var output = File.Create(helper)) await resource.CopyToAsync(output, cancellation);
            string token = Guid.NewGuid().ToString("N");
            var config = new
            {
                installDirectory, work, executable, product, parentId = Environment.ProcessId,
                parentStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O"), token,
                receipt = new Receipt(release.AssetId, release.Digest)
            };
            string configPath = Path.Combine(work, "config.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, Json), cancellation);
            progress.Report("Preparing to restart…");
            worker = StartHelper(helper, configPath, recover: false);
            for (int i = 0; i < 1200; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(work, "ready"))) { handedOff = true; return; }
                if (worker.HasExited) throw new IOException("The installer could not start. Your current installation is unchanged.");
                await Task.Delay(100, cancellation);
            }
            throw new IOException("The update installer did not become ready. Your current installation is unchanged.");
        }
        finally
        {
            if (!handedOff)
            {
                if (worker is { HasExited: false }) { worker.Kill(); worker.WaitForExit(); }
                // The worker has not modified files until our process exits.
                string pending = Path.Combine(installDirectory, PendingName);
                if (File.Exists(pending))
                {
                    var owner = JsonSerializer.Deserialize<Pending>(File.ReadAllText(pending), Json);
                    if (owner?.Work == work) File.Delete(pending);
                }
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            worker?.Dispose();
        }
    }

    public static string SafePath(string root, string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ') ||
            Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe file path in update package.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(parts)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes its folder.");
        for (string? current = full; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Updates cannot traverse symbolic links or junctions.");
        }
        return full;
    }

    public static void ExtractPackage(string archive, string stage, string product, string executable, string version)
    {
        Directory.CreateDirectory(stage);
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string path = SafePath(stage, entry.FullName);
            if (!names.Add(path) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The update contains duplicate files or symbolic links.");
            total += entry.Length;
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The extracted update is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        var manifest = ValidateManifest(stage, product, executable, verifyHashes: true);
        if (ParseVersion(manifest.Version) != ParseVersion(version)) throw new InvalidDataException("The package version does not match its release.");
        var expected = manifest.Files.Keys.Append(ManifestName).Select(p => SafePath(stage, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(names)) throw new InvalidDataException("The update file inventory is incomplete.");
    }

    static Manifest ValidateManifest(string root, string product, string executable, bool verifyHashes)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(SafePath(root, ManifestName)), Json)
            ?? throw new InvalidDataException("Missing update file inventory.");
        if (manifest.Product != product || manifest.Files.Count == 0 || !manifest.Files.ContainsKey(executable) ||
            !manifest.Files.ContainsKey(Path.ChangeExtension(executable, ".dll")) ||
            !manifest.Files.ContainsKey(Path.ChangeExtension(executable, ".deps.json")) ||
            !manifest.Files.ContainsKey(Path.ChangeExtension(executable, ".runtimeconfig.json")))
            throw new InvalidDataException("The update is for a different application or is missing required files.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in manifest.Files)
        {
            string path = SafePath(root, pair.Key);
            if (!names.Add(path) || pair.Key.StartsWith(".easy-", StringComparison.OrdinalIgnoreCase) || pair.Key == ManifestName ||
                !Regex.IsMatch(pair.Value, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("Invalid update file inventory.");
            if (verifyHashes)
            {
                using var stream = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), pair.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An extracted update file failed verification.");
            }
        }
        return manifest;
    }

    static Process StartHelper(string helper, string config, bool recover)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(helper)! };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-ConfigPath", config }) start.ArgumentList.Add(arg);
        if (recover) start.ArgumentList.Add("-Recover");
        return Process.Start(start) ?? throw new IOException("Could not start the update installer.");
    }

    // Called before settings, registry changes or single-instance signalling.
    public static bool HandleStartup(string[] args)
    {
        string pendingPath = Path.Combine(AppContext.BaseDirectory, PendingName);
        if (!File.Exists(pendingPath)) return false;
        var pending = ReadPending(pendingPath);
        if (pending is null && !File.Exists(pendingPath)) return false;
        pending = pending
            ?? throw new IOException("Invalid interrupted-update record.");
        int index = Array.IndexOf(args, "--update-complete");
        if (index >= 0 && index + 1 < args.Length && args[index + 1] == pending.Token) return false;
        try
        {
            using var worker = Process.GetProcessById(pending.WorkerId);
            if (worker.StartTime.ToUniversalTime().ToString("O") == pending.WorkerStarted && !worker.HasExited) return true;
        }
        catch (ArgumentException) { }
        using var recovery = StartHelper(Path.Combine(pending.Work, "apply.ps1"), Path.Combine(pending.Work, "config.json"), recover: true);
        return true;
    }

    public static void AcknowledgeStartup(string[] args)
    {
        int index = Array.IndexOf(args, "--update-complete");
        if (index < 0 || index + 1 >= args.Length) return;
        string path = Path.Combine(AppContext.BaseDirectory, PendingName);
        if (!File.Exists(path)) return;
        var pending = ReadPending(path);
        if (pending?.Token == args[index + 1]) File.WriteAllText(Path.Combine(pending.Work, "healthy"), Environment.ProcessId.ToString());
    }

    static Pending? ReadPending(string path)
    {
        try { return JsonSerializer.Deserialize<Pending>(File.ReadAllText(path), Json); }
        catch (FileNotFoundException) { return null; } // The installer just completed.
    }

    public static string? LastFailure()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ".easy-update-result.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            string message = "The last update failed; your previous build was restored. " + data.RootElement.GetProperty("error").GetString();
            File.Delete(path);
            return message;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    sealed record Receipt(long AssetId, string Digest);
    sealed record Pending(string Work, string Token, int WorkerId, string WorkerStarted);
    sealed record Manifest(string Product, string Version, DateTimeOffset BuiltUtc, Dictionary<string, string> Files, string? BuildId);
}
