using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SVNRevDesk.Core.Svn;

// All commands use ArgumentList rather than a shell. File names and URLs are never interpreted as code.
public sealed class SvnRunner : IDisposable
{
    private static readonly Encoding NativeTextEncoding = GetNativeTextEncoding();
    // SVN reads --targets files in the Windows locale, independently of log-message --encoding.
    internal static Encoding TargetFileEncoding => Encoding.GetEncoding(NativeTextEncoding.CodePage,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    private readonly SemaphoreSlim commandGate = new(1, 1);
    public string Executable { get; set; } = SvnExecutable.DefaultName;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ConfigurationDirectory { get; set; }
    public string? ConfigurationTemplateDirectory { get; set; }
    public event Action<string>? CommandCompleted;
    public event Action<SvnProgress>? ProgressChanged;

    public async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default,
        string? outputFile = null, string? workingDirectory = null, long? maximumOutputBytes = null)
    {
        var args = arguments.ToList();
        if (args.Count == 0) throw new ArgumentException("缺少 SVN 命令。", nameof(arguments));
        if (maximumOutputBytes is <= 0 || maximumOutputBytes != null && outputFile == null)
            throw new ArgumentException("输出大小限制必须为正数，并指定输出文件。", nameof(maximumOutputBytes));
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            return await RunProcessAsync(args, outputFile, workingDirectory, maximumOutputBytes, cancellationToken);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private async Task<string> RunProcessAsync(List<string> arguments, string? outputFile, string? workingDirectory,
        long? maximumOutputBytes, CancellationToken cancellationToken)
    {
        var executablePath = await SvnExecutable.ResolveAsync(Executable, cancellationToken: cancellationToken);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = arguments.Contains("--xml") ? Encoding.UTF8 : NativeTextEncoding,
            StandardErrorEncoding = NativeTextEncoding,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        if (workingDirectory != null) startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--non-interactive");
        if (ConfigurationDirectory != null)
        {
            PrepareConfigurationDirectory(ConfigurationDirectory);
            startInfo.ArgumentList.Add("--config-dir");
            startInfo.ArgumentList.Add(ConfigurationDirectory);
        }
        if (!string.IsNullOrWhiteSpace(Username))
        {
            startInfo.ArgumentList.Add("--username");
            startInfo.ArgumentList.Add(Username);
        }
        if (Password != null)
        {
            // Do not expose passwords in process arguments, settings, or the SVN auth cache.
            startInfo.ArgumentList.Add("--password-from-stdin");
            startInfo.ArgumentList.Add("--no-auth-cache");
        }

        using var process = new Process { StartInfo = startInfo };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"无法启动 SVN：{executablePath}。请检查程序旁的 Tools/Svn 文件夹，或在设置中指定 svn.exe。", ex);
        }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

        ProgressChanged?.Invoke(new(arguments[0], SvnProgressStage.Started));

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        // Drain both pipes before waiting for exit, including for large diffs and binary downloads.
        var outputTask = outputFile == null
            ? ReadOutputAsync(process, arguments, cancellationToken)
            : CopyOutputAsync(process, outputFile, arguments[0], maximumOutputBytes, cancellationToken);
        try
        {
            if (Password != null) await process.StandardInput.WriteLineAsync(Password.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new SvnException(arguments[0], process.ExitCode, error);
            ProgressChanged?.Invoke(new(arguments[0], SvnProgressStage.Completed));
            CommandCompleted?.Invoke($"{DateTime.Now:HH:mm:ss}  {arguments[0]} 完成");
            return output;
        }
        catch
        {
            // Cancelling a directory read must release the lane only after its process and pipes stop.
            // Otherwise the next operation could overlap a process still accessing the working copy.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(outputTask, errorTask); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private void PrepareConfigurationDirectory(string directory)
    {
        var marker = Path.Combine(directory, ".svnclient-initialized");
        if (File.Exists(marker)) return;
        try
        {
            Directory.CreateDirectory(directory);
            // Import the existing SVN profile once, preserving cached credentials and trusted
            // certificates on this machine. Never overwrite an already configured portable file.
            if (ConfigurationTemplateDirectory != null && Directory.Exists(ConfigurationTemplateDirectory)
                && !Path.GetFullPath(ConfigurationTemplateDirectory).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                CopyMissingConfiguration(ConfigurationTemplateDirectory, directory, Path.GetFullPath(directory));
            File.WriteAllText(marker, "SVNRevDesk configuration initialized.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法准备 SVN 配置目录：{directory}。请检查程序目录的读写权限。", ex);
        }
    }

    private static void CopyMissingConfiguration(string source, string destination, string portableRoot)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            // The executable itself can be placed inside the old profile directory.
            if (Path.GetFullPath(path).Equals(portableRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            var target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path))
            {
                Directory.CreateDirectory(target);
                CopyMissingConfiguration(path, target, portableRoot);
            }
            else if (!File.Exists(target)) File.Copy(path, target);
        }
    }

    private async Task<string> ReadOutputAsync(Process process, List<string> arguments, CancellationToken token)
    {
        // Query results and file contents are data, not progress messages.
        var streamNotifications = !arguments.Contains("--xml")
            && arguments[0] is "update" or "checkout" or "commit" or "switch" or "merge" or "export";
        if (!streamNotifications) return await process.StandardOutput.ReadToEndAsync(token);

        var output = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            output.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line))
                ProgressChanged?.Invoke(new(arguments[0], SvnProgressStage.Output, line));
        }
        return output.ToString();
    }

    private async Task<string> CopyOutputAsync(Process process, string outputFile, string command,
        long? maximumOutputBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            long transferred = 0;
            var lastReport = Stopwatch.StartNew();
            ProgressChanged?.Invoke(new(command, SvnProgressStage.Transfer));
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (maximumOutputBytes is { } limit && count > limit - transferred)
                    throw new InvalidDataException("文件超过允许读取的大小，请直接打开或导出文件。");
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                transferred += count;
                // Large files must not flood the dispatcher with a notification for every buffer.
                if (lastReport.ElapsedMilliseconds < 150) continue;
                ProgressChanged?.Invoke(new(command, SvnProgressStage.Transfer, BytesTransferred: transferred));
                lastReport.Restart();
            }
            ProgressChanged?.Invoke(new(command, SvnProgressStage.Transfer, BytesTransferred: transferred));
            return "";
        }
        catch
        {
            // Stop the producer when its output is rejected; otherwise it can block
            // on a full pipe while RunProcessAsync is waiting for it to exit.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
    }

    // An empty peg revision prevents '@' in a real file name from becoming revision syntax.
    public static string Target(string path) => path + "@";
    public static string AtRevision(string url, string revision) => url + "@" + revision;
    public void Dispose() => commandGate.Dispose();

    private static Encoding GetNativeTextEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)GetACP());
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
