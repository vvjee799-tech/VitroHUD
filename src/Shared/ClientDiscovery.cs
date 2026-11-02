using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VitroHUD;

// Installation identity and live capabilities are independent of release numbers.
internal static class ClientDiscovery
{
    private static readonly HashSet<int> observed = [];
    private static string Normalize(string value) => Regex.Replace(Regex.Replace(Regex.Replace(value.ToLowerInvariant().Trim(), @"\s*\((?:x64|x86|arm64|64-bit|32-bit)\)$", ""), @"\s+v?\d+(?:\.\d+)+(?:[-+][\w.-]+)?$", ""), @"[\s._-]", "");
    internal static string[] ExecutableNames(string provider) => provider switch
    {
        "DeepSeek" => ["DeepSeek Harness.exe", "DeepSeekHarness.exe", "deepseek-harness.exe", "dsh-desktop.exe", "DSH Desktop.exe"],
        "Claude" => ["Claude.exe"],
        "ChatGPT" => ["ChatGPT.exe", "Codex.exe"],
        _ => []
    };
    internal static bool MatchesName(string provider, string value) => provider switch
    {
        "DeepSeek" => Normalize(value) is "deepseekharness" or "dshdesktop" or "@deepseekai/dshdesktop",
        "Claude" => Normalize(value) is "claude" or "claudedesktop",
        "ChatGPT" => Normalize(value) is "chatgpt" or "codex" or "openaicodex" or "openaichatgptdesktop",
        _ => false
    };
    internal static bool MatchesIdentity(string provider, string fileName, string product, string description, string originalName)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (Regex.IsMatch(stem, "setup|installer|uninstall|update|crashpad|helper", RegexOptions.IgnoreCase)) return false;
        return MatchesName(provider, product) || MatchesName(provider, description)
            || MatchesName(provider, stem) && MatchesName(provider, Path.GetFileNameWithoutExtension(originalName));
    }
    internal static bool IsExecutable(string provider, string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            return MatchesIdentity(provider, Path.GetFileName(path), info.ProductName ?? "", info.FileDescription ?? "", info.OriginalFilename ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
    }
    internal static string? SavedPath(string provider)
    {
        try
        {
            var file = new FileInfo(AppPaths.FilePath("client-paths.json"));
            if (!file.Exists || file.Length > 16000) return null;
            var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file.FullName));
            return paths?.TryGetValue(provider, out var path) == true && IsExecutable(provider, path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal static void Remember(string provider, string path)
    {
        if (!IsExecutable(provider, path)) throw new InvalidOperationException("程序未识别为对应客户端主程序。");
        var file = AppPaths.FilePath("client-paths.json");
        using var gate = new Mutex(false, @"Local\VitroHUD.ClientPaths." + Wire.Hash(Environment.UserName));
        var locked = false;
        try
        {
            try { locked = gate.WaitOne(1500); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) return;
            var paths = new Dictionary<string, string>();
            try
            {
                if (new FileInfo(file) is { Exists: true, Length: < 16000 })
                    paths = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            path = Path.GetFullPath(path);
            if (paths.TryGetValue(provider, out var previous) && string.Equals(path, previous, StringComparison.OrdinalIgnoreCase)) return;
            paths[provider] = path;
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(paths));
            File.Move(file + ".tmp", file, true);
            DiagnosticLog.Write(LogEvent.ClientDiscovered, provider, clientPath: path);
        }
        finally { if (locked) gate.ReleaseMutex(); }
    }
    internal static void TryRemember(string provider, string path)
    {
        try { Remember(provider, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { }
    }
    internal static string? ProcessPath(string provider, int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            if (path == null || !IsExecutable(provider, path)) return null;
            lock (observed) if (observed.Add(processId)) DiagnosticLog.Write(LogEvent.ClientObserved, provider, clientPath: path);
            TryRemember(provider, path);
            return path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
    internal static bool IsRunning(string provider, string path) => RunningExecutables(provider).Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
    internal static List<(int ProcessId, string Path)> RunningExecutables(string provider)
    {
        var result = new List<(int, string)>();
        var saved = SavedPath(provider);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    // Metadata is inspected for visible renamed/portable clients too.
                    if (!MatchesName(provider, process.ProcessName) && process.MainWindowHandle == 0
                        && !string.Equals(Path.GetFileNameWithoutExtension(saved), process.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                    var path = process.MainModule?.FileName;
                    if (path != null && IsExecutable(provider, path)) result.Add((process.Id, path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return result;
    }
    internal static string? FindExecutable(string provider)
    {
        var running = RunningExecutables(provider).Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (running.Length == 1) { TryRemember(provider, running[0]); return running[0]; }
        var saved = SavedPath(provider);
        if (saved != null) return saved;
        if (running.Length > 1) return null; // Do not launch a third copy among competing installations.
        var path = RegistryCandidates(provider).Concat(DefaultCandidates(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(p => IsExecutable(provider, p));
        path ??= ShortcutCandidates(provider).FirstOrDefault(p => IsExecutable(provider, p));
        if (path != null) TryRemember(provider, path);
        return path;
    }
    internal static IEnumerable<string> DefaultCandidates(string provider)
    {
        var folders = ExecutableNames(provider).Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
            foreach (var folder in folders)
                foreach (var path in DirectoryCandidates(provider, Path.Combine(root, folder!))) yield return path;
    }
    internal static IEnumerable<string> DirectoryCandidates(string provider, string directory)
    {
        foreach (var name in ExecutableNames(provider)) yield return Path.Combine(directory, name);
        string[] versions;
        try
        {
            versions = Directory.Exists(directory) ? Directory.GetDirectories(directory, "app-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(8).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var version in versions)
            foreach (var name in ExecutableNames(provider)) yield return Path.Combine(version, name);
    }
    // Registry values are parsed as paths, never executed as command lines.
    internal static string? RegistryExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        var match = value.StartsWith('"') ? Regex.Match(value, "^\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase)
            : Regex.Match(value, @"^(.+?\.exe)(?=\s|,|$)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var path = match.Groups[1].Value;
        return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) ? path : null;
    }
    internal static List<string> RegistryCandidates(string provider)
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var name in ExecutableNames(provider))
                    {
                        try
                        {
                            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                            if (RegistryExecutable(key?.GetValue(null) as string) is { } path) result.Add(path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                    }
                    using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                    {
                        try
                        {
                            using var key = uninstall!.OpenSubKey(name);
                            if (!MatchesName(provider, key?.GetValue("DisplayName") as string ?? "")) continue;
                            if (RegistryExecutable(key?.GetValue("DisplayIcon") as string) is { } icon) result.Add(icon);
                            if (key?.GetValue("InstallLocation") is string rawDirectory)
                            {
                                var directory = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                                if (Path.IsPathFullyQualified(directory) && !directory.StartsWith(@"\\", StringComparison.Ordinal))
                                    result.AddRange(DirectoryCandidates(provider, directory));
                            }
                            if (RegistryExecutable(key?.GetValue("UninstallString") as string) is { } uninstaller)
                                result.AddRange(DirectoryCandidates(provider, Path.GetDirectoryName(uninstaller)!));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
            }
        return result;
    }
    internal static List<string> ShortcutCandidates(string provider, IEnumerable<string>? directories = null)
    {
        var result = new List<string>();
        // Resolve installed launchers; never invoke shortcuts or execute their arguments.
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return result;
            shell = Activator.CreateInstance(type);
            directories ??= new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }.Select(Environment.GetFolderPath);
            foreach (var directory in directories)
            {
                if (directory.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(directory)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(directory, "*.lnk", options).Take(1500))
                {
                    object? shortcut = null;
                    try
                    {
                        shortcut = ((dynamic)shell!).CreateShortcut(file);
                        string target = ((dynamic)shortcut).TargetPath;
                        if (!Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                        if (IsExecutable(provider, target)) result.Add(target);
                        else if (MatchesName(provider, Path.GetFileNameWithoutExtension(file)))
                            result.AddRange(DirectoryCandidates(provider, Path.GetDirectoryName(target)!));
                    }
                    catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    finally { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        return result;
    }
}
