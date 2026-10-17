using System.Diagnostics;
using System.Runtime.InteropServices;
using SVNRevDesk.Core.Svn;

internal static class SvnExecutableTests
{
    public static async Task RunAsync(string root, Action<bool, string> assert)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "Svn", "bin", "svn.exe");
        assert(File.Exists(bundled) && SvnExecutable.Resolve("svn.exe") == bundled,
            "Build includes SVN and the default resolves beside the application");
        assert(SvnExecutable.Resolve(" SVN.EXE ") == bundled && SvnExecutable.Resolve(null) == bundled,
            "Default SVN lookup handles whitespace, case, and an empty setting");
        assert(SvnExecutable.Resolve(Path.Combine(root, "previous-computer", "svn.exe")) == bundled,
            "A missing custom SVN path falls back to the bundled client after relocation");

        var customDirectory = Path.Combine(root, "custom");
        Directory.CreateDirectory(customDirectory);
        var customPath = Path.Combine(customDirectory, "svn.exe");
        File.Copy(bundled, customPath);
        assert(SvnExecutable.Resolve(customPath) == customPath
            && SvnExecutable.Resolve(Path.Combine("custom", "svn.exe"), root) == customPath,
            "Valid absolute and application-relative custom SVN paths retain priority");
        assert(SvnExecutable.Resolve("svn.exe", Path.Combine(root, "without-bundle")) == "svn.exe",
            "A development host without a bundle can still use SVN from PATH");

        // Run the same test host from a relocated directory, with SVN absent from PATH.
        // This also exercises real process startup and native dependencies, not only path strings.
        var relocatedDirectory = Path.Combine(root, "relocated application 中文");
        var unrelatedDirectory = Path.Combine(root, "unrelated working directory");
        Directory.CreateDirectory(relocatedDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(relocatedDirectory, Path.GetFileName(file)));
        var testHost = Path.Combine(relocatedDirectory, "SVNRevDesk.IntegrationTests.exe");
        var startInfo = new ProcessStartInfo(testHost)
        {
            WorkingDirectory = unrelatedDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--probe-bundled-svn");
        startInfo.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the relocated test host");
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        assert(process.ExitCode == 0 && output.Contains("Bundled SVN probe passed")
            && !Directory.Exists(Path.Combine(relocatedDirectory, "Svn")),
            "Relocated application extracts and starts embedded SVN without native files, SVN on PATH, or a matching working directory: " + error.Trim());
    }

    public static async Task<int> ProbeAsync(string? applicationDirectory = null)
    {
        var directory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var expected = Path.Combine(directory, "Tools", "Svn", "bin", "svn.exe");
        if (await SvnExecutable.ResolveAsync("svn.exe", directory) != expected) return 1;
        using var runner = new SvnRunner { Executable = expected };
        var version = await runner.RunAsync(["--version", "--quiet"]);
        var modules = await runner.RunAsync(["--version"]);
        if (!version.StartsWith("1.14.5") || !modules.Contains("ra_local") || !modules.Contains("ra_serf")
            || !modules.Contains("ra_svn")) return 1;
        // Berkeley DB is loaded only for some repository/authentication backends. Load it
        // explicitly so a successful file:// probe cannot hide its extra C++ dependencies.
        var nativeDirectory = Path.GetDirectoryName(expected)!;
        var library = NativeLibrary.Load(Path.Combine(nativeDirectory, "SlikSvn-DB44-20-x64.dll"),
            typeof(SvnExecutableTests).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            {
                var module = currentProcess.Modules.Cast<ProcessModule>()
                    .Single(item => item.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!module.FileName.Equals(Path.Combine(nativeDirectory, name), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Native dependency loaded from outside the bundle: {module.FileName}");
            }
        }
        finally { NativeLibrary.Free(library); }
        Console.WriteLine("Bundled SVN probe passed");
        return 0;
    }
}
