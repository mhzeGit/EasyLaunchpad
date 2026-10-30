using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyTools.Updating;

namespace EasyTools.Tests;

public sealed class AppUpdaterTests
{
    static UpdateRelease Release(string version = "1.2.0", long id = 20, string digest = "sha256:abc") =>
        new(version, id, digest, "https://github.com/owner/app/releases/download/v1.2.0/app.zip", 10, DateTimeOffset.UtcNow);

    [Fact]
    public void DetectsNewVersionsAndReplacedBuildsWithoutDowngrading()
    {
        Assert.True(AppUpdater.IsNewer(Release(), "1.1.0", 20, "sha256:abc", DateTimeOffset.MaxValue));
        Assert.False(AppUpdater.IsNewer(Release(), "2.0.0", 1, "old", DateTimeOffset.MinValue));
        Assert.False(AppUpdater.IsNewer(Release(), "1.2.0", 20, "sha256:ABC", DateTimeOffset.MinValue));
        Assert.True(AppUpdater.IsNewer(Release(), "1.2.0", 19, "sha256:abc", DateTimeOffset.MaxValue));
        Assert.True(AppUpdater.IsNewer(Release(), "1.2.0", 20, "different", DateTimeOffset.MaxValue));
        Assert.True(AppUpdater.IsNewer(Release(), "1.2.0", null, null, DateTimeOffset.MinValue));
        Assert.False(AppUpdater.IsNewer(Release(), "1.2.0", null, null, DateTimeOffset.MaxValue));
        Assert.False(AppUpdater.IsNewer(Release("1.2.0.0"), "1.2.0", 20, "sha256:abc", DateTimeOffset.MinValue));
        var identified = Release() with { BuildId = new string('a', 64) };
        Assert.False(AppUpdater.IsNewer(identified, "1.2.0", null, null, DateTimeOffset.MinValue, new string('A', 64)));
        Assert.False(AppUpdater.IsNewer(identified, "1.2.0", 19, "previous-zip", DateTimeOffset.MinValue, identified.BuildId));
        Assert.True(AppUpdater.IsNewer(identified, "1.2.0", null, null, DateTimeOffset.MinValue, new string('b', 64)));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("/outside")]
    [InlineData("ui/../../outside")]
    [InlineData("ui/file:stream")]
    [InlineData("ui/file.")]
    [InlineData("ui/file ")]
    [InlineData("CON.txt")]
    [InlineData("ui//file")]
    [InlineData(".\\file")]
    public void RejectsUnsafePackagePaths(string path) =>
        Assert.Throws<InvalidDataException>(() => AppUpdater.SafePath(Path.GetTempPath(), path));

    [Fact]
    public void SelectsOnlyStableApplicationAssetWithGitHubChecksum()
    {
        var data = new
        {
            draft = false, prerelease = false, tag_name = "v1.2.0",
            assets = new[] { new { name = "App-1.2.0-win-x64.zip", state = "uploaded", id = 42,
                digest = "sha256:" + new string('a', 64), browser_download_url = "https://github.com/owner/App/releases/download/v1.2.0/App.zip",
                size = 100, updated_at = "2026-01-01T00:00:00Z", label = "build:" + new string('b', 64) } }
        };
        var root = JsonSerializer.SerializeToElement(data);
        Assert.Equal(42, AppUpdater.ParseRelease(root, "App", "owner/App")!.AssetId);
        Assert.Equal(new string('b', 64), AppUpdater.ParseRelease(root, "App", "owner/App")!.BuildId);
        Assert.Null(AppUpdater.ParseRelease(root, "OtherApp", "owner/App"));
        Assert.Throws<InvalidDataException>(() => AppUpdater.ParseRelease(root, "App", "attacker/App"));
        Assert.Null(AppUpdater.ParseRelease(JsonSerializer.SerializeToElement(withDraft()), "App", "owner/App"));

        object withDraft() => new { draft = true, prerelease = false };
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("tamper")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("version")]
    public void ValidatesCompletePackageBeforeInstallation(string scenario)
    {
        string work = Path.Combine(Path.GetTempPath(), "updater-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string archive = Path.Combine(work, "package.zip");
            var files = new Dictionary<string, string>
            {
                ["App.exe"] = "host", ["App.dll"] = "assembly", ["App.deps.json"] = "deps",
                ["App.runtimeconfig.json"] = "runtime", ["ui/app.js"] = "script"
            };
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                foreach (var pair in files) Add(zip, pair.Key, scenario == "tamper" ? "modified" : pair.Value);
                Add(zip, AppUpdater.ManifestName, JsonSerializer.Serialize(new
                {
                    product = "App", version = scenario == "version" ? "9.0.0" : "1.0.0", builtUtc = DateTimeOffset.UtcNow,
                    files = files.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Value))))
                }));
                if (scenario == "extra") Add(zip, "unlisted.dll", "bad");
                if (scenario == "duplicate") Add(zip, "APP.EXE", "duplicate");
                if (scenario == "traversal") Add(zip, "../outside.txt", "outside");
            }
            void Extract() => AppUpdater.ExtractPackage(archive, Path.Combine(work, "stage"), "App", "App.exe", "1.0.0");
            if (scenario == "valid") { Extract(); Assert.Equal("script", File.ReadAllText(Path.Combine(work, "stage/ui/app.js"))); }
            else Assert.Throws<InvalidDataException>(Extract);
            Assert.False(File.Exists(Path.Combine(work, "outside.txt")));
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    static void Add(ZipArchive zip, string name, string contents)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }
}
