using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SVNRevDesk.Core.Services;

// Stage and verify a native tool before replacing it. Both SVN and WinMerge share
// this installation logic; incomplete packages are never published.
internal sealed class EmbeddedToolPackage(string name, string resourcePrefix, string relativeDirectory,
    string executable, string backupName, string temporaryPrefix, string? legacyRelativeDirectory = null,
    IReadOnlyList<string>? preservedFiles = null)
{
    private const string VersionFile = ".bundled-version";
    private readonly Lazy<PackageManifest> manifest = new(() => ReadManifest(resourcePrefix, executable));
    private readonly SemaphoreSlim installGate = new(1, 1);
    private readonly HashSet<string> readyDirectories = new(StringComparer.OrdinalIgnoreCase);

    public async Task EnsureInstalledAsync(string applicationDirectory, CancellationToken token)
    {
        var directory = GetPackagePath(applicationDirectory, relativeDirectory);
        await installGate.WaitAsync(token);
        try
        {
            if (readyDirectories.Contains(directory) && FilesExist(directory, manifest.Value.Files)) return;
            await Task.Run(() => Install(applicationDirectory, directory, token), token);
            readyDirectories.Add(directory);
        }
        catch (IOException ex)
        { throw new InvalidOperationException($"无法准备内置 {name}：{ex.Message}。请将程序放在可写目录；若文件被占用，请关闭同目录中的 {name} 后重试。", ex); }
        catch (UnauthorizedAccessException ex)
        { throw new InvalidOperationException($"无法释放内置 {name}。请将程序放在有写入权限的目录。", ex); }
        finally { installGate.Release(); }
    }

    private void Install(string applicationDirectory, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureNoLinks(directory);
        Directory.CreateDirectory(applicationDirectory);
        using var installationLock = AcquireInstallationLock(applicationDirectory, token);
        MigrateLegacyDirectory(applicationDirectory, directory, token);
        var package = manifest.Value;
        if (FilesMatch(directory, package.Files, token)) { WriteVersion(directory, package.Version); return; }
        var staging = GetPackagePath(applicationDirectory, "." + temporaryPrefix + "-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            Extract(staging, package.Files, token);
            if (!FilesMatch(staging, package.Files, token)) throw new InvalidDataException($"内置 {name} 文件校验失败，请重新生成或获取完整程序。");
            WriteVersion(staging, package.Version);
            token.ThrowIfCancellationRequested();
            // User settings live alongside the tool but are not part of the bundled manifest.
            foreach (var relativePath in preservedFiles ?? [])
            {
                var source = GetPackagePath(directory, relativePath);
                if (!File.Exists(source)) continue;
                EnsureNoLinks(source);
                var destination = GetPackagePath(staging, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            token.ThrowIfCancellationRequested();
            PublishDirectory(applicationDirectory, staging, directory, token);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void MigrateLegacyDirectory(string applicationDirectory, string directory, CancellationToken token)
    {
        if (legacyRelativeDirectory == null || Directory.Exists(directory) || File.Exists(directory)) return;
        var legacyDirectory = GetPackagePath(applicationDirectory, legacyRelativeDirectory);
        if (!Directory.Exists(legacyDirectory)) return;
        // Only recognize an old tool installation; leave unrelated folders alone.
        if (!File.Exists(GetPackagePath(legacyDirectory, executable))
            && !File.Exists(GetPackagePath(legacyDirectory, VersionFile))) return;
        EnsureNoLinks(legacyDirectory);
        EnsureNoLinks(directory);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        // Move the whole directory so additional files survive. The normal verification
        // below will repair an incomplete installation and retain it in Backups.
        MoveDirectory(legacyDirectory, directory, token);
    }

    private FileStream AcquireInstallationLock(string applicationDirectory, CancellationToken token)
    {
        var path = GetPackagePath(applicationDirectory, "." + temporaryPrefix + "-install.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { token.WaitHandle.WaitOne(100); }
        }
    }

    private static bool FilesExist(string directory, IReadOnlyList<PackageFile> files) =>
        files.All(file => File.Exists(GetPackagePath(directory, file.Path)));

    private static bool FilesMatch(string directory, IReadOnlyList<PackageFile> files, CancellationToken token)
    {
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var path = GetPackagePath(directory, file.Path);
            if (!File.Exists(path)) return false;
            EnsureNoLinks(path);
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void Extract(string staging, IReadOnlyList<PackageFile> files, CancellationToken token)
    {
        using var resource = OpenResource(resourcePrefix + ".zip");
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        var expected = files.ToDictionary(file => file.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[128 * 1024];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            if (!expected.Remove(entry.FullName.Replace('\\', '/'))) throw new InvalidDataException($"内置 {name} 包含重复或未登记文件：{entry.FullName}");
            var destination = GetPackagePath(staging, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            int count;
            while ((count = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
        }
        if (expected.Count > 0) throw new InvalidDataException($"内置 {name} 压缩包缺少清单中的文件。");
    }

    private void PublishDirectory(string applicationDirectory, string staging, string directory, CancellationToken token)
    {
        EnsureNoLinks(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        string? backup = null;
        if (Directory.Exists(directory))
        {
            backup = GetPackagePath(applicationDirectory, Path.Combine("Backups", backupName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
            EnsureNoLinks(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            MoveDirectory(directory, backup, token);
        }
        try { MoveDirectory(staging, directory, token); }
        catch { if (backup != null && !Directory.Exists(directory)) MoveDirectory(backup, directory, CancellationToken.None); throw; }
    }

    private static void MoveDirectory(string source, string destination, CancellationToken token)
    {
        // Windows scanners can briefly hold freshly extracted native binaries.
        // Retry only transient access/sharing errors; preserve the original failure otherwise.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 20
                && ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                token.WaitHandle.WaitOne(100);
            }
        }
    }

    private static string GetPackagePath(string directory, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) throw new InvalidDataException("内置工具包含绝对路径。");
        var path = Path.GetFullPath(normalized, root);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("内置工具路径超出了程序目录。");
        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException($"内置工具路径包含链接或目录联接：{current}"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void WriteVersion(string directory, string version)
    {
        var path = GetPackagePath(directory, VersionFile);
        if (!File.Exists(path) || File.ReadAllText(path) != version) File.WriteAllText(path, version);
    }

    private static PackageManifest ReadManifest(string prefix, string executable)
    {
        using var stream = OpenResource(prefix + ".manifest.json");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var files = JsonSerializer.Deserialize<PackageFile[]>(json) ?? throw new InvalidDataException("内置工具缺少文件清单。");
        if (!files.Any(file => file.Path.Replace('\\', '/') == executable.Replace('\\', '/'))) throw new InvalidDataException("内置工具清单缺少可执行文件。");
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), files);
    }

    private static Stream OpenResource(string name) => typeof(EmbeddedToolPackage).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException($"缺少内置工具资源：{name}");
    private sealed record PackageFile(string Path, string Sha256);
    private sealed record PackageManifest(string Version, IReadOnlyList<PackageFile> Files);
}
