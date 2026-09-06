// Codex Portable Launcher
// Build target: Windows x64, .NET Framework 4.8, C# 5 compatible.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

[assembly: AssemblyTitle("LF Portable")]
[assembly: AssemblyDescription("LF portable launcher for Codex Desktop")]
[assembly: AssemblyCompany("LF")]
[assembly: AssemblyProduct("LF Portable")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyVersion("1.4.24.22")]
[assembly: AssemblyFileVersion("1.4.24.22")]
[assembly: ComVisible(false)]

namespace CodexPortable
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // A loader failure from a briefly disconnected removable drive must
            // return an exit code to the launcher instead of blocking behind a
            // system-owned Application Error dialog.
            NativeMethods.SetErrorMode(NativeMethods.SemFailCriticalErrors |
                NativeMethods.SemNoGpFaultErrorBox);
            string rootOverride = null;
            string rootTokenOverride = null;
            string bootstrapperPathOverride = null;
            int bootstrapperProcessId = 0;
            bool autoStart = false;
            List<string> forwardedArgs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--portable-root", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length) return 41;
                    rootOverride = args[++i];
                }
                else if (args[i].StartsWith("--portable-root=", StringComparison.OrdinalIgnoreCase))
                {
                    rootOverride = args[i].Substring("--portable-root=".Length);
                }
                else if (string.Equals(args[i], "--portable-root-token", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length) return 41;
                    rootTokenOverride = args[++i];
                }
                else if (args[i].StartsWith("--portable-root-token=", StringComparison.OrdinalIgnoreCase))
                {
                    rootTokenOverride = args[i].Substring("--portable-root-token=".Length);
                }
                else if (string.Equals(args[i], "--bootstrapper-pid", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.None,
                        CultureInfo.InvariantCulture, out bootstrapperProcessId) || bootstrapperProcessId <= 0)
                        return 41;
                }
                else if (args[i].StartsWith("--bootstrapper-pid=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(args[i].Substring("--bootstrapper-pid=".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out bootstrapperProcessId) || bootstrapperProcessId <= 0)
                        return 41;
                }
                else if (string.Equals(args[i], "--bootstrapper-path", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length) return 41;
                    bootstrapperPathOverride = args[++i];
                }
                else if (args[i].StartsWith("--bootstrapper-path=", StringComparison.OrdinalIgnoreCase))
                {
                    bootstrapperPathOverride = args[i].Substring("--bootstrapper-path=".Length);
                    if (string.IsNullOrEmpty(bootstrapperPathOverride)) return 41;
                }
                else if (string.Equals(args[i], "--auto-start", StringComparison.OrdinalIgnoreCase))
                {
                    autoStart = true;
                }
                else forwardedArgs.Add(args[i]);
            }
            args = forwardedArgs.ToArray();
            bool hasBootstrapContext = !string.IsNullOrEmpty(rootOverride) ||
                !string.IsNullOrEmpty(rootTokenOverride) ||
                !string.IsNullOrEmpty(bootstrapperPathOverride) || bootstrapperProcessId > 0;
            if (hasBootstrapContext && (string.IsNullOrEmpty(rootOverride) ||
                string.IsNullOrEmpty(rootTokenOverride) ||
                string.IsNullOrEmpty(bootstrapperPathOverride) || bootstrapperProcessId <= 0))
                return 41;
            PortableLayout layout = PortableLayout.FromExecutable(rootOverride, rootTokenOverride);
            LauncherLocale.Load(layout);
            if (bootstrapperProcessId == Process.GetCurrentProcess().Id ||
                (bootstrapperProcessId > 0 && !IsBootstrapperForLayout(bootstrapperProcessId,
                    bootstrapperPathOverride, layout)))
                return 41;

            // The launcher is a handoff tool, not a second desktop shell. If the
            // portable Codex process is already running, leave it alone and exit
            // without showing another launcher window.
            bool created;
            // Scope the launcher mutex to this normalized portable root. A fixed
            // name would let a test copy or another USB drive block this one.
            string mutexName = PortableProcess.GetMutexName(layout);
            using (Mutex mutex = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    return 2;
                }

                // Perform this check only after acquiring the mutex. Otherwise
                // two launchers can both observe a startup gap and proceed past
                // the preflight before either one serializes on the mutex.
                if (PortableProcess.IsDesktopRunning(layout)) return 3;

                try
                {
                    PortableBranding.InitializeProcessIdentity();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new PortableForm(layout, autoStart));
                    return 0;
                }
                catch (Exception ex)
                {
                    SafeLog.TryWrite(layout, "fatal", ex);
                    MessageBox.Show(LauncherLocale.T("启动器发生错误。请使用“生成诊断”查看日志。\r\n\r\n错误类型：" + ex.GetType().Name,
                        "The launcher encountered an error. Create a diagnostic report.\r\n\r\nError type: " + ex.GetType().Name),
                        "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }

        private static bool IsBootstrapperForLayout(int processId, string bootstrapperPath,
            PortableLayout layout)
        {
            if (processId <= 0 || string.IsNullOrEmpty(bootstrapperPath) || layout == null) return false;
            Process process = null;
            try
            {
                process = Process.GetProcessById(processId);
                string executable;
                if (!PortableProcess.TryGetExecutablePath(process, out executable)) return false;
                string actual = Path.GetFullPath(executable).TrimEnd('\\');
                string expected = Path.GetFullPath(bootstrapperPath).TrimEnd('\\');
                string root = Path.GetFullPath(layout.Root).TrimEnd('\\');
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return false;
                string parent = Path.GetDirectoryName(actual);
                return string.Equals(parent == null ? "" : parent.TrimEnd('\\'), root,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally { if (process != null) process.Dispose(); }
        }

    }

    internal static class PortableProcess
    {
        internal static string GetMutexName(PortableLayout layout)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            return GetMutexNameForRootToken(layout.RootToken);
        }

        internal static string GetMutexNameForRootToken(string rootToken)
        {
            if (!JobRun.IsRootToken(rootToken)) throw new ArgumentException("rootToken");
            return "Global\\CodexPortable-Desktop-" + rootToken;
        }

        internal static string GetRootToken(PortableLayout layout)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            if (!JobRun.IsRootToken(layout.RootToken)) throw new InvalidDataException("portable root token");
            return layout.RootToken;
        }

        internal static string GetRootToken(string portableRoot)
        {
            if (string.IsNullOrEmpty(portableRoot)) throw new ArgumentException("portableRoot");
            string fullRoot = Path.GetFullPath(portableRoot).ToUpperInvariant();
            string volumeRoot = Path.GetPathRoot(fullRoot);
            uint serial;
            uint maximumComponentLength;
            uint flags;
            if (string.IsNullOrEmpty(volumeRoot) || !NativeMethods.GetVolumeInformation(volumeRoot,
                null, 0, out serial, out maximumComponentLength, out flags, null, 0))
                throw new IOException("Portable root volume identity is unavailable.");
            string relative = fullRoot.Length > volumeRoot.Length ?
                fullRoot.Substring(volumeRoot.Length).TrimEnd('\\') : "";
            string identity = "vol:" + serial.ToString("X8", CultureInfo.InvariantCulture) +
                "|path:" + relative;

            byte[] input = Encoding.UTF8.GetBytes(identity);
            byte[] digest = null;
            try
            {
                using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(input);
                StringBuilder token = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    token.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return "root-" + token.ToString();
            }
            finally
            {
                Array.Clear(input, 0, input.Length);
                if (digest != null) Array.Clear(digest, 0, digest.Length);
            }
        }

        // Serializes operations that replace directories shared with the
        // detached desktop process.  The UI mutex only covers the launcher
        // window lifetime; this second mutex must also cover the handoff and
        // command-line cache repair after the window has closed.
        internal static Mutex AcquireMutationMutex(PortableLayout layout, int timeoutMilliseconds)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            return AcquireMutationMutexForRootToken(layout.RootToken, timeoutMilliseconds);
        }

        internal static Mutex AcquireMutationMutexForRootToken(string rootToken,
            int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Mutex mutex = new Mutex(false, GetMutexNameForRootToken(rootToken) + "-mutation");
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(timeoutMilliseconds, false); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }
                return mutex;
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        internal static void ReleaseMutationMutex(Mutex mutex)
        {
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }

        internal static bool IsDesktopRunning(PortableLayout layout)
        {
            string portableDesktop;
            try
            {
                // Electron child processes use the same executable as the
                // desktop shell. Restrict detection to the portable executable
                // so an installed official ChatGPT desktop never blocks this
                // portable root.
                portableDesktop = NormalizeExecutablePath(layout.AppExe);
            }
            catch { return true; }

            bool jobExists;
            bool jobActive;
            if (!JobRun.TryGetRootJobStateForToken(layout.RootToken, out jobExists, out jobActive)) return true;
            // A concurrent launcher can briefly own the root Job during handoff.
            // Treat that lease as occupied until its last handle closes.
            if (jobExists || jobActive) return true;

            int currentProcessId;
            try { currentProcessId = Process.GetCurrentProcess().Id; }
            catch { return true; }
            Process[] processes;
            try
            {
                // Only the portable desktop uses this executable name. Avoid
                // opening and probing every process on the machine, including
                // unrelated installed ChatGPT instances.
                processes = Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(PortableBranding.DesktopExecutableName));
            }
            catch { return true; }
            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                try
                {
                    if (process.Id == currentProcessId) continue;
                    string executable;
                    if (!TryGetExecutablePath(process, out executable))
                    {
                        // The portable desktop has a unique process name. If
                        // an elevated instance denies executable-path access,
                        // keep the same-root duplicate-start check fail-closed;
                        // the installed WindowsApps desktop uses ChatGPT.exe.
                        if (string.Equals(process.ProcessName, "CodexDesktop",
                            StringComparison.OrdinalIgnoreCase)) return true;
                        continue;
                    }
                    if (IsSameExecutablePath(executable, portableDesktop)) return true;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }

        private static string NormalizeExecutablePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static bool IsSameExecutablePath(string candidate, string expected)
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { return false; }
            return string.Equals(full.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetExecutablePath(Process process, out string executable)
        {
            executable = null;
            try
            {
                ProcessModule module = process.MainModule;
                if (module != null && !string.IsNullOrEmpty(module.FileName))
                {
                    executable = module.FileName;
                    return true;
                }
            }
            catch { }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation,
                    false, unchecked((uint)process.Id));
                if (handle == IntPtr.Zero) return false;
                return TryGetExecutablePath(handle, out executable);
            }
            catch { return false; }
            finally
            {
                if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle);
            }
        }

        internal static bool TryGetExecutablePath(IntPtr processHandle, out string executable)
        {
            executable = null;
            if (processHandle == IntPtr.Zero) return false;
            try
            {
                uint length = NativeMethods.MaximumProcessImagePath;
                StringBuilder buffer = new StringBuilder((int)length);
                if (!NativeMethods.QueryFullProcessImageName(processHandle, 0, buffer, ref length) ||
                    length == 0) return false;
                executable = buffer.ToString();
                return !string.IsNullOrEmpty(executable);
            }
            catch { return false; }
        }
    }

    internal static class PluginCacheRecovery
    {
        private const long MaxManifestBytes = 4L * 1024L * 1024L;

        private static string ToExtendedPath(string path)
        {
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
            string full;
            bool driveAbsolute = path.Length >= 3 && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/');
            bool uncAbsolute = path.StartsWith("\\\\", StringComparison.Ordinal);
            // Legacy .NET Framework may throw before GetFullPath can normalize an
            // already-absolute path longer than MAX_PATH.  All long paths reaching
            // this class are composed below a validated portable root, so retain the
            // absolute form and let the Win32 extended-path API handle it.
            if (path.Length >= 240 && (driveAbsolute || uncAbsolute))
                full = path.Replace('/', '\\');
            else full = Path.GetFullPath(path);
            // Keep ordinary paths ordinary.  Some older Windows/.NET combinations reject the
            // extended prefix for short paths unless the process manifest opts into long paths.
            if (full.Length < 240) return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }

        private static bool DirectoryExists(string path)
        {
            string extended = ToExtendedPath(path);
            try { if (Directory.Exists(extended)) return true; } catch { }
            uint attributes = NativeMethods.GetFileAttributes(extended);
            return attributes != NativeMethods.InvalidFileAttributes &&
                (attributes & (uint)FileAttributes.Directory) != 0;
        }

        private static bool FileExists(string path)
        {
            string extended = ToExtendedPath(path);
            try { if (File.Exists(extended)) return true; } catch { }
            uint attributes = NativeMethods.GetFileAttributes(extended);
            return attributes != NativeMethods.InvalidFileAttributes &&
                (attributes & (uint)FileAttributes.Directory) == 0;
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = GetAttributes(ToNativePath(path));
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                // A path that cannot be inspected is not safe to treat as a
                // trusted cache entry. Callers use this as a fail-closed check.
                return true;
            }
        }

        private static void RejectExistingReparsePoint(string path, string label)
        {
            if (!DirectoryExists(path) && !FileExists(path)) return;
            if (IsReparsePoint(path))
                throw new IOException(label + " cannot be a reparse point: " + path);
        }

        private static string ToNativePath(string path)
        {
            string full = ToExtendedPath(path);
            if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }

        private sealed class PluginDefinition
        {
            internal string PluginName;
            internal string MarketplaceName;
            internal string Version;
            internal string SourceRoot;
            internal string CacheCatalogRoot;
            internal string CacheBaseRoot;
            internal string CacheVersionRoot;
        }

        internal static bool RequiredPluginCacheComplete(PortableLayout layout, string[] requiredPlugins)
        {
            try
            {
                for (int i = 0; i < requiredPlugins.Length; i++)
                {
                    PluginDefinition definition = ReadDefinition(layout, requiredPlugins[i]);
                    if (!IsCachedVersionComplete(definition)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        internal static int EnsureRequiredPlugins(PortableLayout layout, string[] requiredPlugins)
        {
            Mutex mutation = PortableProcess.AcquireMutationMutex(layout, 0);
            if (mutation == null)
                throw new IOException("Plugin cache recovery is already in progress for this portable root.");
            try
            {
                if (PortableProcess.IsDesktopRunning(layout))
                    throw new IOException("Plugin cache recovery is blocked while portable Codex Desktop is running.");
                int repaired = 0;
                for (int i = 0; i < requiredPlugins.Length; i++)
                {
                    PluginDefinition definition = ReadDefinition(layout, requiredPlugins[i]);
                    if (IsCachedVersionComplete(definition)) continue;
                    try
                    {
                        RepairOne(layout, definition);
                    }
                    catch (Exception ex)
                    {
                        string detail = ex.Message;
                        if (ex.InnerException != null) detail += " Inner=" + ex.InnerException.Message;
                        throw new IOException("Plugin recovery failed for " + requiredPlugins[i] + ": " + detail, ex);
                    }
                    if (!IsCachedVersionComplete(definition))
                        throw new InvalidDataException("Recovered plugin cache failed its manifest check: " + requiredPlugins[i]);
                    repaired++;
                }
                return repaired;
            }
            finally { PortableProcess.ReleaseMutationMutex(mutation); }
        }

        private static PluginDefinition ReadDefinition(PortableLayout layout, string pluginKey)
        {
            int separator = pluginKey.IndexOf('@');
            if (separator <= 0 || separator == pluginKey.Length - 1 || pluginKey.IndexOf('@', separator + 1) >= 0)
                throw new InvalidDataException("Invalid required plugin key: " + pluginKey);

            string pluginName = pluginKey.Substring(0, separator);
            string marketplaceName = pluginKey.Substring(separator + 1);
            string sourceRoot;
            if (string.Equals(marketplaceName, "openai-bundled", StringComparison.Ordinal))
            {
                sourceRoot = Path.Combine(layout.Resources, "plugins", marketplaceName, "plugins", pluginName);
            }
            else if (string.Equals(marketplaceName, "openai-primary-runtime", StringComparison.Ordinal))
            {
                sourceRoot = Path.Combine(layout.CodexHome, "offline-marketplaces", marketplaceName, "plugins", pluginName);
            }
            else throw new InvalidDataException("Required plugin uses an untrusted marketplace: " + pluginKey);

            if (!DirectoryExists(sourceRoot)) throw new DirectoryNotFoundException("Offline plugin source is missing: " + sourceRoot);
            if (IsReparsePoint(sourceRoot))
                throw new IOException("Offline plugin source cannot be a reparse point: " + sourceRoot);
            string sourceManifest = Path.Combine(sourceRoot, ".codex-plugin", "plugin.json");
            if (IsReparsePoint(Path.Combine(sourceRoot, ".codex-plugin")) || IsReparsePoint(sourceManifest))
                throw new IOException("Offline plugin manifest cannot be a reparse point: " + sourceManifest);
            string manifestName;
            string version;
            ReadManifestIdentity(sourceManifest, out manifestName, out version);
            if (!string.Equals(manifestName, pluginName, StringComparison.Ordinal))
                throw new InvalidDataException("Offline plugin manifest name does not match its marketplace entry: " + pluginKey);
            if (!IsSafeVersionSegment(version))
                throw new InvalidDataException("Offline plugin version is not a safe cache directory name: " + pluginKey);

            string pluginsRoot = Path.Combine(layout.CodexHome, "plugins");
            string cacheRoot = Path.Combine(pluginsRoot, "cache");
            string cacheCatalogRoot = Path.Combine(cacheRoot, marketplaceName);
            RejectExistingReparsePoint(pluginsRoot, "Plugin root");
            RejectExistingReparsePoint(cacheRoot, "Plugin cache root");
            RejectExistingReparsePoint(cacheCatalogRoot, "Plugin cache catalog");
            string cacheBaseRoot = Path.Combine(cacheCatalogRoot, pluginName);
            return new PluginDefinition {
                PluginName = pluginName,
                MarketplaceName = marketplaceName,
                Version = version,
                SourceRoot = sourceRoot,
                CacheBaseRoot = cacheBaseRoot,
                CacheVersionRoot = Path.Combine(cacheBaseRoot, version),
                CacheCatalogRoot = cacheCatalogRoot
            };
        }

        private static bool IsCachedVersionComplete(PluginDefinition definition)
        {
            if (!DirectoryExists(definition.CacheVersionRoot)) return false;
            if (IsReparsePoint(definition.CacheCatalogRoot) ||
                IsReparsePoint(definition.CacheBaseRoot) || IsReparsePoint(definition.CacheVersionRoot))
                return false;
            string manifest = Path.Combine(definition.CacheVersionRoot, ".codex-plugin", "plugin.json");
            if (!FileExists(manifest)) return false;
            if (IsReparsePoint(Path.Combine(definition.CacheVersionRoot, ".codex-plugin")) ||
                IsReparsePoint(manifest)) return false;
            try
            {
                string name;
                string version;
                ReadManifestIdentity(manifest, out name, out version);
                if (!string.Equals(name, definition.PluginName, StringComparison.Ordinal) ||
                    !string.Equals(version, definition.Version, StringComparison.Ordinal)) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool IsSafeVersionSegment(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128 || value == "." || value == ".." ||
                string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '+')) return false;
            }
            return true;
        }

        internal static void ReadManifestIdentity(string path, out string name, out string version)
        {
            name = null;
            version = null;
            if (!FileExists(path)) throw new FileNotFoundException("Plugin manifest is missing.", path);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = (int)MaxManifestBytes;
            string manifestText;
            using (FileStream stream = OpenFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                if (stream.Length <= 0 || stream.Length > MaxManifestBytes)
                    throw new InvalidDataException("Plugin manifest size is invalid: " + path);
                manifestText = reader.ReadToEnd();
            }
            Dictionary<string, object> json = serializer.Deserialize<Dictionary<string, object>>(manifestText);
            object nameValue;
            object versionValue;
            if (json == null || !json.TryGetValue("name", out nameValue) || !json.TryGetValue("version", out versionValue))
                throw new InvalidDataException("Plugin manifest lacks name or version: " + path);
            name = Convert.ToString(nameValue, CultureInfo.InvariantCulture);
            version = Convert.ToString(versionValue, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
                throw new InvalidDataException("Plugin manifest name or version is blank: " + path);
        }

        private static void RepairOne(PortableLayout layout, PluginDefinition definition)
        {
            if (PortableProcess.IsDesktopRunning(layout))
                throw new IOException("Plugin cache recovery is blocked while portable Codex Desktop is running.");
            string token = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 10);
            // Keep the transient staging name deliberately short.  Plugin assets can
            // contain very deep paths; a verbose staging prefix can push an otherwise
            // valid path past the legacy .NET Framework limit before the native
            // long-path fallback gets a chance to open it.
            string stagingToken = Guid.NewGuid().ToString("N").Substring(0, 8);
            string pluginsRoot = Path.Combine(layout.CodexHome, "plugins");
            string stagingRoot = Path.Combine(pluginsRoot, ".pr-" + stagingToken);
            string stagedBase = Path.Combine(stagingRoot, definition.MarketplaceName, definition.PluginName);
            string stagedVersion = Path.Combine(stagedBase, definition.Version);
            string repairBackupsRoot = Path.Combine(pluginsRoot, "repair-backups");
            string backupTokenRoot = Path.Combine(repairBackupsRoot, token);
            string backupBase = Path.Combine(backupTokenRoot,
                definition.MarketplaceName, definition.PluginName);
            string failedBase = Path.Combine(backupTokenRoot,
                definition.MarketplaceName, definition.PluginName + ".failed");
            bool targetMoved = false;
            bool activated = false;

            try
            {
                RejectExistingReparsePoint(pluginsRoot, "Plugin root");
                EnsureDirectory(stagedVersion);
                CopyDirectoryPortable(definition.SourceRoot, stagedVersion);
                PluginDefinition stagedDefinition = new PluginDefinition {
                    PluginName = definition.PluginName,
                    MarketplaceName = definition.MarketplaceName,
                    Version = definition.Version,
                    SourceRoot = definition.SourceRoot,
                    CacheCatalogRoot = Path.Combine(stagingRoot, definition.MarketplaceName),
                    CacheBaseRoot = stagedBase,
                    CacheVersionRoot = stagedVersion
                };
                if (!IsCachedVersionComplete(stagedDefinition))
                    throw new InvalidDataException("Staged plugin manifest did not validate: " + definition.PluginName);

                AssertTargetStillPresent(layout);
                if (PortableProcess.IsDesktopRunning(layout))
                    throw new IOException("Portable Codex Desktop started during plugin recovery; no cache replacement was attempted.");
                if (DirectoryExists(definition.CacheBaseRoot))
                {
                    AssertNoReparsePoints(definition.CacheBaseRoot);
                    EnsureDirectory(Path.GetDirectoryName(backupBase));
                    MoveDirectoryVerified(definition.CacheBaseRoot, backupBase);
                    targetMoved = true;
                }
                else if (FileExists(definition.CacheBaseRoot))
                {
                    throw new IOException("Plugin cache target is a file, not a directory: " + definition.CacheBaseRoot);
                }

                EnsureDirectory(Path.GetDirectoryName(definition.CacheBaseRoot));
                RejectExistingReparsePoint(pluginsRoot, "Plugin root");
                RejectExistingReparsePoint(Path.Combine(pluginsRoot, "cache"), "Plugin cache root");
                RejectExistingReparsePoint(definition.CacheCatalogRoot, "Plugin cache catalog");
                MoveDirectoryVerified(stagedBase, definition.CacheBaseRoot);
                activated = true;
                if (!IsCachedVersionComplete(definition))
                    throw new InvalidDataException("Activated plugin cache did not validate: " + definition.PluginName);
            }
            catch
            {
                try
                {
                    if (activated && DirectoryExists(definition.CacheBaseRoot) && !DirectoryExists(failedBase))
                    {
                        EnsureDirectory(Path.GetDirectoryName(failedBase));
                        MoveDirectoryVerified(definition.CacheBaseRoot, failedBase);
                    }
                    if (targetMoved && DirectoryExists(backupBase) && !DirectoryExists(definition.CacheBaseRoot))
                    {
                        MoveDirectoryVerified(backupBase, definition.CacheBaseRoot);
                    }
                }
                catch (Exception rollbackError)
                {
                    SafeLog.TryWrite(layout, "plugin-cache-repair-rollback", rollbackError);
                }
                throw;
            }
            finally
            {
                try
                {
                    if (DirectoryExists(stagingRoot)) IOUtil.DeleteDirectoryWithin(stagingRoot, pluginsRoot);
                }
                catch (Exception cleanupError) { SafeLog.TryWrite(layout, "plugin-cache-repair-cleanup", cleanupError); }
                if (activated && IsCachedVersionComplete(definition))
                {
                    try
                    {
                        if (DirectoryExists(backupTokenRoot))
                            IOUtil.DeleteDirectoryWithin(backupTokenRoot, pluginsRoot);
                        if (DirectoryExists(repairBackupsRoot))
                            NativeMethods.RemoveDirectory(ToNativePath(repairBackupsRoot));
                    }
                    catch (Exception cleanupError)
                    {
                        SafeLog.TryWrite(layout, "plugin-cache-repair-backup-cleanup", cleanupError);
                    }
                }
            }
        }

        private static void AssertTargetStillPresent(PortableLayout layout)
        {
            string root = Path.GetPathRoot(layout.Root);
            if (string.IsNullOrEmpty(root) || !DirectoryExists(root)) throw new IOException("Portable drive disappeared during plugin recovery.");
            if (!DirectoryExists(layout.CodexHome)) throw new IOException("Portable Codex data disappeared during plugin recovery.");
        }

        private static void MoveDirectoryVerified(string source, string destination)
        {
            string extendedSource = ToExtendedPath(source);
            string extendedDestination = ToExtendedPath(destination);
            Exception managedError = null;
            try
            {
                Directory.Move(extendedSource, extendedDestination);
            }
            catch (Exception ex)
            {
                managedError = ex;
                // .NET Framework's Directory.Move can reject an otherwise valid
                // extended path before reaching Win32. MoveFileW accepts the
                // same \?\ representation and keeps the operation atomic.
                if (!NativeMethods.MoveFile(extendedSource, extendedDestination))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("Plugin directory move failed: " + source + " -> " + destination,
                        new Win32Exception(error, managedError.Message));
                }
            }
            if (!DirectoryExists(destination) || DirectoryExists(source))
                throw new IOException("Plugin directory move could not be verified: " + source + " -> " + destination);
        }

        private static void AssertNoReparsePoints(string root)
        {
            if (!DirectoryExists(root)) return;
            string extendedRoot = ToNativePath(root);
            FileAttributes rootAttributes = GetAttributes(extendedRoot);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point is not allowed in plugin cache: " + root);
            List<string> directories = new List<string>();
            List<string> files = new List<string>();
            CollectTree(extendedRoot, extendedRoot, directories, files);
        }

        private static int CopyDirectoryPortable(string sourceRoot, string destinationRoot)
        {
            string extendedSourceRoot = ToNativePath(sourceRoot);
            List<string> sourceDirectories = new List<string>();
            List<string> sourceFiles = new List<string>();
            CollectTree(extendedSourceRoot, extendedSourceRoot, sourceDirectories, sourceFiles);
            sourceDirectories.Sort(StringComparer.OrdinalIgnoreCase);
            sourceFiles.Sort(delegate(string left, string right)
            {
                bool leftManifest = IsManifestPath(left, extendedSourceRoot);
                bool rightManifest = IsManifestPath(right, extendedSourceRoot);
                if (leftManifest != rightManifest) return leftManifest ? 1 : -1;
                return StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });

            for (int i = 0; i < sourceDirectories.Count; i++)
            {
                string relative = RelativePath(extendedSourceRoot, sourceDirectories[i]);
                string destinationDirectory = Path.Combine(destinationRoot, relative);
                try { EnsureDirectory(destinationDirectory); }
                catch (Exception ex) { throw new IOException("Plugin directory create failed: " + destinationDirectory + "; " + ex.Message, ex); }
            }
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                string relative = RelativePath(extendedSourceRoot, sourceFiles[i]);
                string destination = Path.Combine(destinationRoot, relative);
                string parent = ParentPath(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    try { EnsureDirectory(parent); }
                    catch (Exception ex) { throw new IOException("Plugin file parent create failed: " + parent + "; " + ex.Message, ex); }
                }
                CopyFilePortable(sourceFiles[i], destination);
            }
            return sourceFiles.Count;
        }

        // .NET Framework's managed Directory.CreateDirectory has a legacy path
        // parser which can reject a valid \\?\ long path with
        // ArgumentException (the failure is dependent on the process manifest and
        // CLR host).  Fall back to the Win32 wide-character API after recursively
        // creating the parent.  This keeps all actual I/O on the same extended-path
        // representation and works on removable volumes as well as local disks.
        private static void EnsureDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Directory path is blank.", "path");
            string extended = ToExtendedPath(path);
            try
            {
                Directory.CreateDirectory(extended);
                return;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }

            if (DirectoryExists(path)) return;
            string parent = ParentPath(path);
            if (!string.IsNullOrEmpty(parent) && !DirectoryExists(parent))
                EnsureDirectory(parent);
            if (!NativeMethods.CreateDirectory(extended, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 183 && !DirectoryExists(path))
                    throw new Win32Exception(error, "Long-path directory creation failed: " + path);
            }
            if (!DirectoryExists(path))
                throw new IOException("Long-path directory creation could not be verified: " + path);
        }

        private static string ParentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int end = path.Length;
            while (end > 0 && (path[end - 1] == '\\' || path[end - 1] == '/')) end--;
            int separator = -1;
            for (int i = end - 1; i >= 0; i--)
            {
                if (path[i] == '\\' || path[i] == '/') { separator = i; break; }
            }
            if (separator < 0) return null;
            if (separator == 2 && end >= 3 && path[1] == ':') return path.Substring(0, 3);
            if (separator == 0) return path.Substring(0, 1);
            return path.Substring(0, separator);
        }

        private static void CollectTree(string root, string current, List<string> directories, List<string> files)
        {
            string nativeCurrent = ToNativePath(current);
            FileAttributes currentAttributes = GetAttributes(nativeCurrent);
            if ((currentAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point is not allowed in offline plugin source: " + current);
            NativeMethods.WIN32_FIND_DATA data;
            string pattern = nativeCurrent.TrimEnd('\\') + "\\*";
            IntPtr find = NativeMethods.FindFirstFile(pattern, out data);
            if (find == NativeMethods.InvalidHandleValue)
            {
                int firstError = Marshal.GetLastWin32Error();
                if (firstError == 2) return;
                throw new Win32Exception(firstError, "Long-path plugin enumeration failed: " + current);
            }
            try
            {
                bool more = true;
                while (more)
                {
                    string name = data.cFileName;
                    if (name != "." && name != "..")
                    {
                        string child = nativeCurrent.TrimEnd('\\') + "\\" + name;
                        FileAttributes attributes = data.dwFileAttributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Reparse point is not allowed in plugin tree: " + child);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Add(child);
                            CollectTree(root, child, directories, files);
                        }
                        else files.Add(child);
                    }
                    more = NativeMethods.FindNextFile(find, out data);
                    if (!more)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 18) throw new Win32Exception(error, "Long-path plugin enumeration failed: " + current);
                    }
                }
            }
            finally { NativeMethods.FindClose(find); }
        }

        private static FileAttributes GetAttributes(string path)
        {
            uint attributes = NativeMethods.GetFileAttributes(path);
            if (attributes == NativeMethods.InvalidFileAttributes)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Long-path attribute query failed: " + path);
            return (FileAttributes)attributes;
        }

        private static bool IsManifestPath(string path, string root)
        {
            return string.Equals(RelativePath(root, path), Path.Combine(".codex-plugin", "plugin.json"), StringComparison.OrdinalIgnoreCase);
        }

        private static string RelativePath(string root, string path)
        {
            return path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void CopyFilePortable(string source, string destination)
        {
            try
            {
                string extendedSource = ToExtendedPath(source);
                string extendedDestination = ToExtendedPath(destination);
                if (!NativeMethods.CopyFile(extendedSource, extendedDestination, true))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CopyFileW failed.");
            }
            catch (Exception ex)
            {
                throw new IOException("Plugin file copy failed (" + ex.GetType().Name + ",0x" +
                    ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "): " + source + " -> " +
                    destination + "; " + ex.Message, ex);
            }
        }

        private static FileStream OpenFileStream(string path, FileMode mode, FileAccess access,
            FileShare share, int bufferSize, FileOptions options)
        {
            uint desiredAccess = 0;
            if ((access & FileAccess.Read) != 0) desiredAccess |= NativeMethods.GenericRead;
            if ((access & FileAccess.Write) != 0) desiredAccess |= NativeMethods.GenericWrite;
            uint shareMode = 0;
            if ((share & FileShare.Read) != 0) shareMode |= NativeMethods.FileShareRead;
            if ((share & FileShare.Write) != 0) shareMode |= NativeMethods.FileShareWrite;
            if ((share & FileShare.Delete) != 0) shareMode |= NativeMethods.FileShareDelete;
            uint disposition;
            switch (mode)
            {
                case FileMode.CreateNew: disposition = NativeMethods.CreateNew; break;
                case FileMode.Create: disposition = NativeMethods.CreateAlways; break;
                case FileMode.Open: disposition = NativeMethods.OpenExisting; break;
                case FileMode.OpenOrCreate: disposition = NativeMethods.OpenAlways; break;
                case FileMode.Truncate: disposition = NativeMethods.TruncateExisting; break;
                case FileMode.Append: disposition = NativeMethods.OpenAlways; break;
                default: throw new ArgumentOutOfRangeException("mode");
            }
            uint flags = NativeMethods.FileAttributeNormal;
            if ((options & FileOptions.SequentialScan) != 0) flags |= NativeMethods.FileFlagSequentialScan;
            if ((options & FileOptions.WriteThrough) != 0) flags |= NativeMethods.FileFlagWriteThrough;
            IntPtr raw = NativeMethods.CreateFile(ToExtendedPath(path), desiredAccess, shareMode,
                IntPtr.Zero, disposition, flags, IntPtr.Zero);
            if (raw == NativeMethods.InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW failed: " + path);
            SafeFileHandle handle = new SafeFileHandle(raw, true);
            try
            {
                FileStream result = new FileStream(handle, access, bufferSize, false);
                if (mode == FileMode.Append) result.Seek(0, SeekOrigin.End);
                return result;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

    }

    internal enum PortableArchitecture
    {
        Unknown,
        X86,
        X64,
        Arm,
        Arm64
    }

    internal static class ArchitectureInfo
    {
        private const ushort ImageFileMachineI386 = 0x014c;
        private const ushort ImageFileMachineArm = 0x01c4;
        private const ushort ImageFileMachineAmd64 = 0x8664;
        private const ushort ImageFileMachineArm64 = 0xAA64;
        private const ushort ProcessorArchitectureIntel = 0;
        private const ushort ProcessorArchitectureArm = 5;
        private const ushort ProcessorArchitectureAmd64 = 9;
        private const ushort ProcessorArchitectureArm64 = 12;

        internal static PortableArchitecture Current
        {
            get
            {
                try
                {
                    ushort processMachine;
                    ushort nativeMachine;
                    if (NativeMethods.IsWow64Process2(NativeMethods.GetCurrentProcess(),
                        out processMachine, out nativeMachine))
                    {
                        ushort machine = nativeMachine == 0 ? processMachine : nativeMachine;
                        PortableArchitecture fromMachine = FromMachine(machine);
                        if (fromMachine != PortableArchitecture.Unknown) return fromMachine;
                    }
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }
                catch (BadImageFormatException) { }

                try
                {
                    NativeMethods.SYSTEM_INFO info;
                    NativeMethods.GetNativeSystemInfo(out info);
                    PortableArchitecture fromSystem = FromProcessorArchitecture(info.wProcessorArchitecture);
                    if (fromSystem != PortableArchitecture.Unknown) return fromSystem;
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }
                catch (BadImageFormatException) { }
                return Environment.Is64BitOperatingSystem ? PortableArchitecture.X64 : PortableArchitecture.X86;
            }
        }

        internal static string Name
        {
            get { return NameOf(Current); }
        }

        internal static string NameOf(PortableArchitecture architecture)
        {
            switch (architecture)
            {
                case PortableArchitecture.X86: return "x86";
                case PortableArchitecture.X64: return "x64";
                case PortableArchitecture.Arm: return "arm";
                case PortableArchitecture.Arm64: return "arm64";
                default: return "unknown";
            }
        }

        internal static PortableArchitecture ParseName(string name)
        {
            if (string.Equals(name, "x86", StringComparison.OrdinalIgnoreCase)) return PortableArchitecture.X86;
            if (string.Equals(name, "x64", StringComparison.OrdinalIgnoreCase)) return PortableArchitecture.X64;
            if (string.Equals(name, "arm", StringComparison.OrdinalIgnoreCase)) return PortableArchitecture.Arm;
            if (string.Equals(name, "arm64", StringComparison.OrdinalIgnoreCase)) return PortableArchitecture.Arm64;
            return PortableArchitecture.Unknown;
        }

        internal static bool HasOfficialDesktopPayload(PortableArchitecture architecture)
        {
            return architecture == PortableArchitecture.X64 || architecture == PortableArchitecture.Arm64;
        }

        internal static PortableArchitecture FromMachine(ushort machine)
        {
            switch (machine)
            {
                case ImageFileMachineI386: return PortableArchitecture.X86;
                case ImageFileMachineAmd64: return PortableArchitecture.X64;
                case ImageFileMachineArm: return PortableArchitecture.Arm;
                case ImageFileMachineArm64: return PortableArchitecture.Arm64;
                default: return PortableArchitecture.Unknown;
            }
        }

        private static PortableArchitecture FromProcessorArchitecture(ushort architecture)
        {
            switch (architecture)
            {
                case ProcessorArchitectureIntel: return PortableArchitecture.X86;
                case ProcessorArchitectureAmd64: return PortableArchitecture.X64;
                case ProcessorArchitectureArm: return PortableArchitecture.Arm;
                case ProcessorArchitectureArm64: return PortableArchitecture.Arm64;
                default: return PortableArchitecture.Unknown;
            }
        }

        internal static bool IsMachineCompatible(string executable, PortableArchitecture expected)
        {
            try
            {
                using (FileStream stream = File.OpenRead(executable))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64) return false;
                    stream.Seek(0x3c, SeekOrigin.Begin);
                    int peOffset = reader.ReadInt32();
                    if (peOffset < 0 || peOffset > stream.Length - 6) return false;
                    stream.Seek(peOffset + 4, SeekOrigin.Begin);
                    ushort machine = reader.ReadUInt16();
                    return FromMachine(machine) == expected;
                }
            }
            catch { return false; }
        }

        internal static bool IsLauncherFileName(string name)
        {
            return string.Equals(name, "CodexPortable.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CodexPortable.x86.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CodexPortable.x64.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CodexPortable.arm.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CodexPortable.arm64.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class PortableLayout
    {
        internal string Root;
        // Captured once while the portable root is available. Every later
        // same-root operation uses this value instead of querying a removable
        // volume again during the launch handoff.
        internal string RootToken;
        internal string DataRoot;
        internal PortableArchitecture Architecture;
        internal string ArchitectureName;
        internal string AppVariantRoot;
        internal string CurrentApp;
        internal string OfficialAppExe;
        internal string AppExe;
        internal string Resources;
        internal string CodexExe;
        internal string Profile;
        internal string CodexHome;
        internal string SqliteHome;
        internal string ElectronData;
        internal string Home;
        internal string AppData;
        internal string LocalAppData;
        internal string LocalAppDataLow;
        internal string Temp;
        internal string XdgConfig;
        internal string XdgCache;
        internal string XdgData;
        internal string XdgState;
        internal string Runtime;
        internal string Secrets;
        internal string Logs;
        internal string Updates;
        internal string VaultFile;
        internal string PlainKeyFile;
        internal string AuthFile;
        internal string EphemeralMarker;
        internal string AuthBackup;
        internal string ConfigFile;
        internal string GlobalStateFile;
        internal string GlobalStateBackup;
        internal string BaseUrlFile;
        internal string ModelFile;
        internal string ModelCatalogFile;
        internal string PiCacheFile;
        internal string LanguageFile;
        internal string Downloads;
        internal string ChromiumCache;
        internal string CrashDumps;
        internal string Tools;
        internal string Packages;
        internal string CommonPackage;
        internal string BundledDesktopPackage;
        internal string HostScratchRoot;
        internal string HostTemp;
        internal string HostXdgCache;
        internal string HostChromiumCache;
        internal string HostDotnetBundle;
        internal string HostNpmCache;
        internal string HostPipCache;
        internal string HostUvCache;

        internal static PortableLayout FromExecutable(string rootOverride = null,
            string rootTokenOverride = null)
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            string root = string.IsNullOrEmpty(rootOverride) ?
                Path.GetFullPath(Path.GetDirectoryName(exe)) : Path.GetFullPath(rootOverride);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Portable root is missing: " + root);
            PortableLayout p = new PortableLayout();
            p.Root = root;
            if (!string.IsNullOrEmpty(rootTokenOverride) && !JobRun.IsRootToken(rootTokenOverride))
                throw new ArgumentException("portable root token");
            p.RootToken = string.IsNullOrEmpty(rootTokenOverride) ?
                PortableProcess.GetRootToken(root) : rootTokenOverride;
            p.DataRoot = Path.Combine(root, "CodexData");
            p.Tools = Path.Combine(p.DataRoot, "tools");
            p.Packages = Path.Combine(p.DataRoot, "packages");
            p.Architecture = ArchitectureInfo.Current;
            p.ArchitectureName = ArchitectureInfo.NameOf(p.Architecture);
            p.AppVariantRoot = p.Architecture == PortableArchitecture.X64 ?
                Path.Combine(p.DataRoot, "app") :
                Path.Combine(p.Tools, "desktop-payloads", p.ArchitectureName);
            p.CurrentApp = Path.Combine(p.AppVariantRoot, "current");
            // Keep the official MSIX payload name for signature/update compatibility,
            // but run a byte-identical Codex-named copy so the portable process is
            // distinguishable from an installed ChatGPT/Codex package.
            p.OfficialAppExe = Path.Combine(p.CurrentApp, "ChatGPT.exe");
            p.AppExe = Path.Combine(p.CurrentApp, PortableBranding.DesktopExecutableName);
            p.Resources = Path.Combine(p.CurrentApp, "resources");
            p.CodexExe = Path.Combine(p.Resources, "codex.exe");
            p.Profile = Path.Combine(p.DataRoot, "data", "profile");
            p.CodexHome = Path.Combine(p.Profile, ".codex");
            p.SqliteHome = Path.Combine(p.CodexHome, "sqlite");
            p.ElectronData = Path.Combine(p.Profile, "electron");
            // Keep homedir at Profile so Codex's default ~/.cache runtime path is portable.
            p.Home = p.Profile;
            p.AppData = Path.Combine(p.Profile, "appdata", "roaming");
            p.LocalAppData = Path.Combine(p.Profile, "appdata", "local");
            p.LocalAppDataLow = Path.Combine(p.Profile, "appdata", "locallow");
            p.Temp = Path.Combine(p.Profile, "temp");
            p.XdgConfig = Path.Combine(p.Profile, "xdg", "config");
            p.XdgCache = Path.Combine(p.Profile, "xdg", "cache");
            p.XdgData = Path.Combine(p.Profile, "xdg", "data");
            p.XdgState = Path.Combine(p.Profile, "xdg", "state");
            p.Runtime = Path.Combine(p.Profile, ".cache", "codex-runtimes", "codex-primary-runtime");
            p.CommonPackage = Path.Combine(p.Packages, "LFPortable-common.zip");
            p.BundledDesktopPackage = Path.Combine(p.Packages,
                "LFPortable-" + p.ArchitectureName + ".msix");
            p.Secrets = Path.Combine(p.DataRoot, "data", "secrets");
            p.Logs = Path.Combine(p.DataRoot, "logs");
            p.Updates = Path.Combine(p.DataRoot, "updates");
            p.VaultFile = Path.Combine(p.Secrets, "api-key.vault");
            p.PlainKeyFile = Path.Combine(p.Secrets, "api-key.txt");
            p.AuthFile = Path.Combine(p.CodexHome, "auth.json");
            p.EphemeralMarker = Path.Combine(p.Secrets, "ephemeral-auth.json");
            p.AuthBackup = Path.Combine(p.Secrets, "auth.previous.json");
            p.ConfigFile = Path.Combine(p.CodexHome, "config.toml");
            p.GlobalStateFile = Path.Combine(p.CodexHome, ".codex-global-state.json");
            p.GlobalStateBackup = p.GlobalStateFile + ".bak";
            p.BaseUrlFile = Path.Combine(p.DataRoot, "data", "config", "custom-api-url.txt");
            p.ModelFile = Path.Combine(p.DataRoot, "data", "config", "custom-model.txt");
            p.ModelCatalogFile = Path.Combine(p.DataRoot, "data", "config", "model-catalog.json");
            p.PiCacheFile = Path.Combine(p.DataRoot, "data", "config", "pi-model-cache.json");
            p.LanguageFile = Path.Combine(p.DataRoot, "data", "config", "launcher-language.txt");
            p.Downloads = Path.Combine(p.DataRoot, "data", "downloads");
            p.ChromiumCache = Path.Combine(p.Profile, "cache", "chromium");
            p.CrashDumps = Path.Combine(p.Logs, "crash-dumps");
            string hostLocalAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            p.HostScratchRoot = Path.Combine(hostLocalAppData, "LFPortable", "scratch",
                "session-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            p.HostTemp = Path.Combine(p.HostScratchRoot, "temp");
            p.HostXdgCache = Path.Combine(p.HostScratchRoot, "xdg-cache");
            p.HostChromiumCache = Path.Combine(p.HostScratchRoot, "chromium-cache");
            p.HostDotnetBundle = Path.Combine(p.HostScratchRoot, "dotnet-bundle");
            p.HostNpmCache = Path.Combine(p.HostScratchRoot, "npm-cache");
            p.HostPipCache = Path.Combine(p.HostScratchRoot, "pip-cache");
            p.HostUvCache = Path.Combine(p.HostScratchRoot, "uv-cache");
            return p;
        }

        internal void EnsureDirectories()
        {
            string[] dirs = new string[] {
                DataRoot, Path.Combine(DataRoot, "app"), Profile, CodexHome, SqliteHome,
                ElectronData, Home, AppData, LocalAppData, LocalAppDataLow, Temp,
                XdgConfig, XdgCache, XdgData, XdgState, Secrets, Logs, Updates,
                Path.Combine(Profile, "cache"), Path.Combine(Profile, "dotnet"),
                Path.Combine(Profile, "nuget"), Path.Combine(Profile, "gh"),
                Path.Combine(Profile, "npm"), Path.Combine(Profile, "pip"),
                Path.Combine(Profile, "cargo"), Path.Combine(Profile, "rustup")
                , Path.Combine(DataRoot, "data", "config"), Downloads, ChromiumCache, CrashDumps, Tools,
                Packages,
                AppVariantRoot
            };
            for (int i = 0; i < dirs.Length; i++)
                IOUtil.EnsureDirectoryWithinNoReparse(dirs[i], Root);
        }

        internal void EnsureConfig()
        {
            ProviderConfiguration.WriteDeterministicConfig(this);
        }

        internal void EnsureOnboardingSuppressed()
        {
            PortableOnboarding.EnsureSuppressed(this);
        }
    }

    internal enum FirstLaunchPreparationStage
    {
        ValidatingCommonPackage,
        ExtractingCommonRuntime,
        VerifyingCommonRuntime,
        InstallingCommonRuntime,
        CommonRuntimeReady,
        ValidatingDesktopPackage,
        ExtractingDesktopPackage,
        VerifyingAndBrandingDesktop,
        DesktopPayloadReady,
        VerifyingInstalledDesktop,
        VerifyingPluginCache,
        RefreshingModelCatalog,
        StartingDesktop,
        ConfirmingDesktopStart,
        DesktopStarted
    }

    internal sealed class FirstLaunchProgress
    {
        internal FirstLaunchPreparationStage Stage;
        internal long CompletedBytes;
        internal long TotalBytes;
        internal int CompletedFiles;
        internal int TotalFiles;

        internal FirstLaunchProgress(FirstLaunchPreparationStage stage)
            : this(stage, 0, 0, 0, 0)
        {
        }

        internal FirstLaunchProgress(FirstLaunchPreparationStage stage,
            long completedBytes, long totalBytes, int completedFiles, int totalFiles)
        {
            Stage = stage;
            CompletedBytes = completedBytes;
            TotalBytes = totalBytes;
            CompletedFiles = completedFiles;
            TotalFiles = totalFiles;
        }
    }

    internal static class PortableBundle
    {
        private const long MaximumExpandedBytes = 4L * 1024L * 1024L * 1024L;
        private const int MaximumEntries = 100000;
        private const int ExtractionTimeoutMinutes = 45;
        private const int ProgressReportIntervalMilliseconds = 125;
        private static readonly string[] CommonRoots = new string[] {
            "tools/dotnet",
            "tools/gh",
            "data/profile/.cache/codex-runtimes",
            "data/profile/.codex/offline-marketplaces"
        };

        private sealed class ActivatedRoot
        {
            internal string Destination;
            internal string Backup;
            internal bool ExistingMoved;
            internal bool NewMoved;
            internal readonly List<RestoredChild> RestoredChildren =
                new List<RestoredChild>();
        }

        private sealed class RestoredChild
        {
            internal string Backup;
            internal string Destination;
            internal bool Directory;
        }

        internal static bool HasInstallPackages(PortableLayout layout)
        {
            return File.Exists(layout.CommonPackage) && File.Exists(layout.BundledDesktopPackage);
        }

        internal static bool CommonPayloadComplete(PortableLayout layout)
        {
            return File.Exists(Path.Combine(layout.Tools, "dotnet", "dotnet.exe")) &&
                (File.Exists(Path.Combine(layout.Tools, "gh", "bin", "gh.exe")) ||
                    File.Exists(Path.Combine(layout.Tools, "gh", "gh.exe"))) &&
                File.Exists(Path.Combine(layout.Runtime, "dependencies", "node", "bin", "node.exe")) &&
                File.Exists(Path.Combine(layout.Runtime, "dependencies", "python", "python.exe")) &&
                File.Exists(Path.Combine(layout.Runtime, "dependencies", "native", "git", "cmd", "git.exe")) &&
                File.Exists(Path.Combine(layout.CodexHome, "offline-marketplaces", "openai-primary-runtime",
                    ".agents", "plugins", "marketplace.json"));
        }

        internal static bool EnsureReady(PortableLayout layout)
        {
            return EnsureReady(layout, null);
        }

        internal static bool EnsureReady(PortableLayout layout,
            Action<FirstLaunchProgress> progress)
        {
            return EnsureReady(layout, progress, false);
        }

        internal static bool EnsureReady(PortableLayout layout,
            Action<FirstLaunchProgress> progress, bool desktopPayloadPrepared)
        {
            EnsureCommonPayload(layout, progress);
            if (desktopPayloadPrepared || PortableBranding.IsPrepared(layout)) return true;
            EnsureDesktopPayload(layout, progress);
            // StageVerifiedReleasePayload validates and brands the complete
            // tree before atomic activation. Returning this in-memory result
            // lets the handoff path reuse that proof instead of scanning the
            // ASAR a second time immediately afterward.
            return true;
        }

        internal static void EnsureCommonPayload(PortableLayout layout)
        {
            EnsureCommonPayload(layout, null);
        }

        internal static void EnsureCommonPayload(PortableLayout layout,
            Action<FirstLaunchProgress> progress)
        {
            if (CommonPayloadComplete(layout)) return;
            layout.EnsureDirectories();
            Mutex mutation = PortableProcess.AcquireMutationMutex(layout, 0);
            if (mutation == null)
                throw new IOException("Another portable installation or repair is in progress.");
            try
            {
                if (CommonPayloadComplete(layout)) return;
                if (PortableProcess.IsDesktopRunning(layout))
                    throw new IOException("Common runtime installation is blocked while Codex Desktop is running.");
                if (!File.Exists(layout.CommonPackage))
                    throw new FileNotFoundException("The bundled common runtime package is missing.", layout.CommonPackage);
                if (progress != null) progress(new FirstLaunchProgress(FirstLaunchPreparationStage.ValidatingCommonPackage));
                CommonArchiveInfo archiveInfo = ValidateCommonArchive(layout.CommonPackage);
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(layout.DataRoot));
                if (drive.IsReady && drive.AvailableFreeSpace < archiveInfo.ExpandedBytes + 512L * 1024L * 1024L)
                    throw new IOException("Insufficient free space for the common portable runtime.");
                if (progress != null) progress(new FirstLaunchProgress(
                    FirstLaunchPreparationStage.ExtractingCommonRuntime, 0,
                    archiveInfo.ExpandedBytes, 0, archiveInfo.FileCount));
                InstallCommonArchive(layout, archiveInfo, progress);
                if (!CommonPayloadComplete(layout))
                    throw new InvalidDataException("The installed common runtime is incomplete.");
                if (progress != null) progress(new FirstLaunchProgress(FirstLaunchPreparationStage.CommonRuntimeReady));
            }
            finally { PortableProcess.ReleaseMutationMutex(mutation); }
        }

        internal static void EnsureDesktopPayload(PortableLayout layout)
        {
            EnsureDesktopPayload(layout, null);
        }

        internal static void EnsureDesktopPayload(PortableLayout layout,
            Action<FirstLaunchProgress> progress)
        {
            if (PortableBranding.IsPrepared(layout)) return;
            if (!ArchitectureInfo.HasOfficialDesktopPayload(layout.Architecture))
                throw new PlatformNotSupportedException("No desktop package is available for this Windows architecture.");
            if (!File.Exists(layout.BundledDesktopPackage))
                throw new FileNotFoundException("The bundled desktop package is missing.", layout.BundledDesktopPackage);
            Mutex mutation = PortableProcess.AcquireMutationMutex(layout, 0);
            if (mutation == null)
                throw new IOException("Another portable installation or repair is in progress.");
            try
            {
                if (PortableBranding.IsPrepared(layout)) return;
                if (PortableProcess.IsDesktopRunning(layout))
                    throw new IOException("Desktop installation is blocked while Codex Desktop is running.");
                PortablePackage.StageVerifiedReleasePayload(layout, layout.BundledDesktopPackage,
                    layout.Architecture, progress);
                // The caller always revalidates the installed tree immediately
                // before launch, after this mutation lock has been released.
            }
            finally { PortableProcess.ReleaseMutationMutex(mutation); }
        }

        private sealed class CommonArchiveInfo
        {
            internal long ExpandedBytes;
            internal int FileCount;
            internal Dictionary<string, long> Files;
        }

        private static CommonArchiveInfo ValidateCommonArchive(string archivePath)
        {
            using (FileStream stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ValidateCommonArchive(stream);
        }

        private static CommonArchiveInfo ValidateCommonArchive(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("The common runtime package stream must be readable and seekable.",
                    "stream");
            if (stream.Length < 100L * 1024L * 1024L || stream.Length > MaximumExpandedBytes)
                throw new InvalidDataException("The bundled common runtime package size is invalid.");
            stream.Position = 0;
            try
            {
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                    return ValidateCommonArchiveEntries(archive);
            }
            finally { stream.Position = 0; }
        }

        private static CommonArchiveInfo ValidateCommonArchiveEntries(ZipArchive archive)
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> files = new Dictionary<string, long>(StringComparer.Ordinal);
            long expandedBytes = 0;
            int count = 0;
            int fileCount = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                count++;
                if (count > MaximumEntries) throw new InvalidDataException("The common runtime package has too many entries.");
                string relative = NormalizeArchivePath(entry.FullName);
                if (relative.Length == 0) continue;
                bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                ValidateCommonArchivePath(relative, directory);
                if (!IsAllowedCommonPath(relative, directory))
                    throw new InvalidDataException("Unexpected common runtime package entry: " + relative);
                if (!paths.Add(relative))
                    throw new InvalidDataException("Duplicate common runtime package entry: " + relative);
                AssertCommonArchiveEntryAttributes(entry, directory);
                if (!directory)
                {
                    if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - expandedBytes)
                        throw new InvalidDataException("The common runtime package expands beyond its limit.");
                    expandedBytes += entry.Length;
                    fileCount++;
                    files.Add(relative, entry.Length);
                }
            }
            if (count == 0 || expandedBytes < 500L * 1024L * 1024L)
                throw new InvalidDataException("The common runtime package is incomplete.");
            return new CommonArchiveInfo {
                ExpandedBytes = expandedBytes,
                FileCount = fileCount,
                Files = files
            };
        }

        internal static string NormalizeArchivePath(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            normalized = normalized.TrimEnd('/');
            if (normalized.Length == 0) return "";
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.IndexOf(':') >= 0)
                throw new InvalidDataException("The common runtime package contains an absolute path.");
            string[] segments = normalized.Split('/');
            for (int i = 0; i < segments.Length; i++)
                if (segments[i].Length == 0 || segments[i] == "." || segments[i] == "..")
                    throw new InvalidDataException("The common runtime package contains an unsafe path.");
            return normalized;
        }

        internal static bool IsAllowedCommonPath(string path, bool directory)
        {
            // Plugin caches are derived after both trusted source packages are
            // installed. The unused F# SDK subtree is omitted to keep the GitHub
            // release below its hard asset-size limit without removing C#/VB.
            if (IsExcludedCommonPath(path)) return false;
            for (int i = 0; i < CommonRoots.Length; i++)
            {
                string root = CommonRoots[i];
                if (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return true;
                if (directory && (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    root.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        private static bool IsExcludedCommonPath(string path)
        {
            const string sdkPrefix = "tools/dotnet/sdk/";
            if (!path.StartsWith(sdkPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            int versionEnd = path.IndexOf('/', sdkPrefix.Length);
            if (versionEnd < 0 || versionEnd == path.Length - 1) return false;
            string relative = path.Substring(versionEnd + 1);
            return relative.Equals("FSharp", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("FSharp/", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateCommonArchivePath(string relative, bool directory)
        {
            string[] segments = relative.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 0 || segment == "." || segment == ".." ||
                    segment.Length > 255 || segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal) ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    IsReservedCommonWindowsName(segment))
                    throw new InvalidDataException("The common runtime package contains an unsafe Windows path.");
                if (directory || i < segments.Length - 1)
                    for (int c = 0; c < segment.Length; c++) if (segment[c] > 127)
                        throw new InvalidDataException("The common runtime package contains a non-ASCII directory name.");
            }
        }

        private static bool IsReservedCommonWindowsName(string value)
        {
            string stem = value;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            stem = stem.ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                stem == "CLOCK$") return true;
            return stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.Ordinal) ||
                 stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] >= '1' && stem[3] <= '9';
        }

        private static void AssertCommonArchiveEntryAttributes(ZipArchiveEntry entry,
            bool directory)
        {
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            uint unixType = (attributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (attributes & 0x400) != 0)
                throw new InvalidDataException("Links are not allowed in the common runtime package.");
            if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                throw new InvalidDataException("The common runtime package contains an unsupported entry type.");
            if (directory && unixType == 0x8000)
                throw new InvalidDataException("A common runtime directory entry has a file type.");
            if (!directory && unixType == 0x4000)
                throw new InvalidDataException("A common runtime file entry has a directory type.");
        }

        private static void InstallCommonArchive(PortableLayout layout, CommonArchiveInfo archiveInfo,
            Action<FirstLaunchProgress> progress)
        {
            string transaction = Path.Combine(layout.Updates, "common-" +
                Guid.NewGuid().ToString("N").Substring(0, 10));
            string staging = Path.Combine(transaction, "stage");
            string backupRoot = Path.Combine(transaction, "backup");
            string failedRoot = Path.Combine(transaction, "failed");
            List<ActivatedRoot> activated = new List<ActivatedRoot>();
            bool retain = false;
            try
            {
                Directory.CreateDirectory(staging);
                ExtractCommonArchiveWithTar(layout.CommonPackage, staging, archiveInfo, progress);
                if (progress != null) progress(new FirstLaunchProgress(FirstLaunchPreparationStage.VerifyingCommonRuntime));
                AssertNoReparsePoints(staging);
                AssertCommonFiles(staging);
                if (progress != null) progress(new FirstLaunchProgress(FirstLaunchPreparationStage.InstallingCommonRuntime));
                for (int i = 0; i < CommonRoots.Length; i++)
                {
                    string relative = CommonRoots[i].Replace('/', Path.DirectorySeparatorChar);
                    string source = Path.Combine(staging, relative);
                    string destination = Path.Combine(layout.DataRoot, relative);
                    RejectReparseAncestry(destination, layout.DataRoot);
                    string backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    ActivatedRoot record = new ActivatedRoot { Destination = destination, Backup = backup };
                    activated.Add(record);
                    if (Directory.Exists(destination))
                    {
                        Directory.Move(destination, backup);
                        record.ExistingMoved = true;
                        // The package owns the files it supplies, but the root
                        // may also contain user-created runtimes, catalogs, or
                        // other entries.  Validate the old tree before moving
                        // any of those entries back into the new package root.
                        AssertNoReparsePoints(backup);
                    }
                    Directory.Move(source, destination);
                    record.NewMoved = true;
                    if (record.ExistingMoved)
                        RestoreUnownedChildren(backup, destination,
                            record.RestoredChildren);
                }
                AssertCommonFiles(layout.DataRoot);
            }
            catch (Exception installationError)
            {
                Exception rollbackError = null;
                for (int i = activated.Count - 1; i >= 0; i--)
                {
                    ActivatedRoot record = activated[i];
                    try
                    {
                        if (record.NewMoved && Directory.Exists(record.Destination))
                        {
                            RestoreChildrenForRollback(record.RestoredChildren);
                            string failed = Path.Combine(failedRoot, i.ToString(CultureInfo.InvariantCulture));
                            Directory.CreateDirectory(Path.GetDirectoryName(failed));
                            Directory.Move(record.Destination, failed);
                        }
                        if (record.ExistingMoved && Directory.Exists(record.Backup))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(record.Destination));
                            Directory.Move(record.Backup, record.Destination);
                        }
                    }
                    catch (Exception ex) { rollbackError = ex; }
                }
                if (rollbackError != null)
                {
                    retain = true;
                    throw new IOException("Common runtime installation failed and rollback needs inspection at " +
                        transaction + ".", new AggregateException(installationError, rollbackError));
                }
                throw;
            }
            finally
            {
                if (!retain && Directory.Exists(transaction))
                    IOUtil.DeleteDirectoryWithin(transaction, layout.Updates);
            }
        }

        private static void RestoreUnownedChildren(string backup, string destination,
            List<RestoredChild> restored)
        {
            string[] children = Directory.GetFileSystemEntries(backup, "*",
                SearchOption.TopDirectoryOnly);
            for (int i = 0; i < children.Length; i++)
            {
                string child = children[i];
                string name = Path.GetFileName(child);
                if (string.IsNullOrEmpty(name))
                    throw new IOException("Common runtime backup contains an invalid child path.");
                string target = Path.Combine(destination, name);
                bool childDirectory = Directory.Exists(child);
                bool targetDirectory = Directory.Exists(target);
                bool targetFile = File.Exists(target);
                if (!targetDirectory && !targetFile)
                {
                    if (childDirectory) Directory.Move(child, target);
                    else File.Move(child, target);
                    restored.Add(new RestoredChild {
                        Backup = child,
                        Destination = target,
                        Directory = childDirectory
                    });
                    continue;
                }
                if (childDirectory && targetDirectory)
                {
                    RestoreUnownedChildren(child, target, restored);
                    continue;
                }
                if (childDirectory != targetDirectory)
                    throw new IOException(
                        "Common runtime update would replace a user entry with a different path type: " +
                        target);
                // A file at the same relative path is package-owned and is
                // intentionally replaced by the new archive content.
            }
        }

        private static void RestoreChildrenForRollback(List<RestoredChild> restored)
        {
            for (int i = restored.Count - 1; i >= 0; i--)
            {
                RestoredChild child = restored[i];
                if (!CommonPathExists(child.Destination))
                    throw new IOException("Common runtime rollback child is missing: " +
                        child.Destination);
                if (CommonPathExists(child.Backup))
                    throw new IOException("Common runtime rollback backup is occupied: " +
                        child.Backup);
                if (child.Directory) Directory.Move(child.Destination, child.Backup);
                else File.Move(child.Destination, child.Backup);
            }
            restored.Clear();
        }

        private static bool CommonPathExists(string path)
        {
            return Directory.Exists(path) || File.Exists(path);
        }

        private static void ExtractCommonArchiveWithTar(string archivePath, string staging,
            CommonArchiveInfo expected, Action<FirstLaunchProgress> progress)
        {
            string tar = FindSystemTar();
            using (FileStream source = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (ZipArchive archive = new ZipArchive(source, ZipArchiveMode.Read, false))
            {
                CommonArchiveInfo current = ValidateCommonArchiveEntries(archive);
                AssertSameCommonArchive(expected, current);

                object sync = new object();
                HashSet<string> reportedFiles = new HashSet<string>(StringComparer.Ordinal);
                StringBuilder diagnostics = new StringBuilder();
                Stopwatch reporter = Stopwatch.StartNew();
                long completedBytes = 0;
                int completedFiles = 0;
                Action<string> receiveLine = delegate(string line)
                {
                    if (string.IsNullOrEmpty(line)) return;
                    bool shouldReport = false;
                    long reportBytes = 0;
                    int reportFiles = 0;
                    if (line.StartsWith("x ", StringComparison.Ordinal))
                    {
                        string relative;
                        try { relative = NormalizeArchivePath(line.Substring(2)); }
                        catch { relative = string.Empty; }
                        long length;
                        bool matchedFile = false;
                        lock (sync)
                        {
                            if (relative.Length != 0 && current.Files.TryGetValue(relative, out length) &&
                                reportedFiles.Add(relative))
                            {
                                matchedFile = true;
                                completedBytes = checked(completedBytes + length);
                                completedFiles++;
                                if (reporter.ElapsedMilliseconds >= ProgressReportIntervalMilliseconds ||
                                    completedFiles == current.FileCount)
                                {
                                    shouldReport = true;
                                    reportBytes = completedBytes;
                                    reportFiles = completedFiles;
                                    reporter.Restart();
                                }
                            }
                        }
                        // A verbose directory line is expected, but a line that
                        // looks like an extracted file and does not match the
                        // validated archive must remain visible in the failure
                        // diagnostics instead of being silently treated as progress.
                        if (!matchedFile &&
                            (relative.Length == 0 || !line.EndsWith("/", StringComparison.Ordinal)))
                        {
                            lock (sync)
                            {
                                if (diagnostics.Length < 32768)
                                {
                                    if (diagnostics.Length != 0) diagnostics.Append(" | ");
                                    diagnostics.Append(line);
                                }
                            }
                        }
                    }
                    else
                    {
                        lock (sync)
                        {
                            if (diagnostics.Length < 32768)
                            {
                                if (diagnostics.Length != 0) diagnostics.Append(" | ");
                                diagnostics.Append(line);
                            }
                        }
                    }
                    if (shouldReport && progress != null)
                        progress(new FirstLaunchProgress(
                            FirstLaunchPreparationStage.ExtractingCommonRuntime,
                            reportBytes, current.ExpandedBytes, reportFiles, current.FileCount));
                };

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = tar;
                // exFAT rejects several timestamps present in the runtime
                // package. Content extraction must not depend on restoring
                // archive metadata, so ask bsdtar to leave mtimes untouched.
                info.Arguments = "-xvmf " + IOUtil.QuoteArgument(archivePath) + " -C " +
                    IOUtil.QuoteArgument(staging);
                info.WorkingDirectory = staging;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = new Process())
                {
                    process.StartInfo = info;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) receiveLine(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) receiveLine(e.Data);
                    };
                    if (!process.Start()) throw new InvalidOperationException("Windows tar.exe could not start.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(ExtractionTimeoutMinutes * 60 * 1000))
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(); } catch { }
                        throw new TimeoutException("Common runtime extraction timed out.");
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        string message;
                        lock (sync) { message = diagnostics.ToString(); }
                        throw new InvalidDataException("Windows tar.exe rejected the common runtime package" +
                            (message.Length == 0 ? "." : ": " + message));
                    }
                }
                PortablePackage.AssertExtractedTreeNoReparse(staging, current.ExpandedBytes,
                    current.FileCount);
                if (progress != null) progress(new FirstLaunchProgress(
                    FirstLaunchPreparationStage.ExtractingCommonRuntime,
                    current.ExpandedBytes, current.ExpandedBytes,
                    current.FileCount, current.FileCount));
            }
        }

        private static void AssertSameCommonArchive(CommonArchiveInfo expected,
            CommonArchiveInfo actual)
        {
            if (expected == null || actual == null || expected.ExpandedBytes != actual.ExpandedBytes ||
                expected.FileCount != actual.FileCount || expected.Files.Count != actual.Files.Count)
                throw new InvalidDataException("The common runtime package changed before extraction.");
            foreach (KeyValuePair<string, long> file in expected.Files)
            {
                long length;
                if (!actual.Files.TryGetValue(file.Key, out length) || length != file.Value)
                    throw new InvalidDataException("The common runtime package changed before extraction.");
            }
        }

        private static string FindSystemTar()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
            {
                string native = Path.Combine(windows, "Sysnative", "tar.exe");
                if (File.Exists(native)) return native;
            }
            string system = Path.Combine(windows, "System32", "tar.exe");
            if (File.Exists(system)) return system;
            throw new FileNotFoundException("Windows tar.exe is required for common runtime extraction.", system);
        }

        private static void AssertCommonFiles(string root)
        {
            string[] required = new string[] {
                "tools/dotnet/dotnet.exe",
                "data/profile/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe",
                "data/profile/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe",
                "data/profile/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/cmd/git.exe",
                "data/profile/.codex/offline-marketplaces/openai-primary-runtime/.agents/plugins/marketplace.json"
            };
            for (int i = 0; i < required.Length; i++)
            {
                string path = Path.Combine(root, required[i].Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new InvalidDataException("Common runtime is missing: " + required[i]);
            }
            if (!File.Exists(Path.Combine(root, "tools", "gh", "bin", "gh.exe")) &&
                !File.Exists(Path.Combine(root, "tools", "gh", "gh.exe")))
                throw new InvalidDataException("Common runtime is missing GitHub CLI.");
        }

        private static void AssertNoReparsePoints(string root)
        {
            PortablePackage.AssertExtractedTreeNoReparse(root);
        }

        private static void RejectReparseAncestry(string path, string root)
        {
            string current = Path.GetFullPath(path).TrimEnd('\\');
            string limit = Path.GetFullPath(root).TrimEnd('\\');
            if (!current.Equals(limit, StringComparison.OrdinalIgnoreCase) &&
                !current.StartsWith(limit + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Common runtime destination is outside the portable root.");
            while (current.Length >= limit.Length)
            {
                if (Directory.Exists(current) &&
                    (new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Common runtime destination is beneath a reparse point: " + current);
                if (current.Equals(limit, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) break;
            }
        }
    }

    internal static class PortableOnboarding
    {
        private const int MaxGlobalStateBytes = 4 * 1024 * 1024;
        private const string PersistedAtomStateKey = "electron-persisted-atom-state";
        private const string OnboardingOverrideKey = "electron:onboarding-override";
        private const string ProjectlessCompletedKey = "electron:onboarding-projectless-completed";
        private const string WelcomePendingKey = "electron:onboarding-welcome-pending";
        private const string SeenModelUpgradeListKey = "seen-model-upgrade-list";
        private const string LatestModelSeenKey = "latest-model-seen";
        private const string CurrentModelUpgrade = ProviderConfiguration.DefaultModel;
        // The desktop release currently advertises gpt-5.6-sol on first run.
        // Keep that announcement acknowledged while the portable default stays
        // on gpt-5.6-terra, otherwise the desktop shows its initial "Try model"
        // CTA before the launcher can apply the rest of the onboarding state.
        private const string OfficialAnnouncedModelUpgrade = "gpt-5.6-sol";
        private const string AgentModeByHostIdKey = "agent-mode-by-host-id";
        private const string LocalHostId = "local";
        // The official desktop enum calls the "config.toml" UI mode "custom".
        // Writing "config.toml" here would be rejected by the desktop schema.
        private const string ConfigTomlAgentMode = "custom";
        private const string EnabledReasoningEffortsKey = "enabled-reasoning-efforts";
        private const string KnowledgeWorkAnnouncementKey = "has-seen-knowledge-work-announcement";
        private const string FastModeAnnouncementKey = "has-seen-fast-mode-announcement";
        private const string WorkPluginsAnnouncementKey = "has-seen-work-plugins-announcement";
        private const string WalletAnnouncementKey = "wallet-onboarding-announcement-dismissed-v1";
        private static readonly string[] SupportedReasoningEfforts = new string[] {
            "low", "medium", "high", "xhigh", "max", "ultra"
        };

        internal static void EnsureSuppressed(PortableLayout layout)
        {
            Directory.CreateDirectory(layout.CodexHome);
            Dictionary<string, object> state = ReadWithBackup(layout.GlobalStateFile,
                layout.GlobalStateBackup);
            Dictionary<string, object> atoms = GetOrCreateObject(state, PersistedAtomStateKey);
            bool changed = SetIfDifferent(atoms, OnboardingOverrideKey, "app");
            changed |= SetIfDifferent(atoms, ProjectlessCompletedKey, true);
            changed |= SetIfDifferent(atoms, WelcomePendingKey, false);
            changed |= SetIfDifferent(atoms, KnowledgeWorkAnnouncementKey, true);
            changed |= SetIfDifferent(atoms, FastModeAnnouncementKey, true);
            changed |= SetIfDifferent(atoms, WorkPluginsAnnouncementKey, true);
            changed |= SetIfDifferent(atoms, WalletAnnouncementKey, true);

            object latestModel;
            if (atoms.TryGetValue(LatestModelSeenKey, out latestModel))
            {
                string legacyModel = latestModel as string;
                if (!string.IsNullOrEmpty(legacyModel))
                    changed |= EnsureStringInArray(atoms, SeenModelUpgradeListKey, legacyModel);
            }
            changed |= EnsureStringInArray(atoms, SeenModelUpgradeListKey, CurrentModelUpgrade);
            changed |= EnsureStringInArray(atoms, SeenModelUpgradeListKey,
                OfficialAnnouncedModelUpgrade);
            string configuredModel = ProviderConfiguration.ReadEffectiveModel(layout);
            if (ProviderConfiguration.IsValidModel(configuredModel))
                changed |= EnsureStringInArray(atoms, SeenModelUpgradeListKey, configuredModel);
            List<string> catalogModels = ProviderConfiguration.ReadCatalogModelIds(layout);
            for (int i = 0; i < catalogModels.Count; i++)
                changed |= EnsureStringInArray(atoms, SeenModelUpgradeListKey, catalogModels[i]);
            changed |= SetIfDifferent(atoms, LatestModelSeenKey, null);

            Dictionary<string, object> agentModes = GetOrCreateObject(atoms,
                AgentModeByHostIdKey);
            changed |= SetIfDifferent(agentModes, LocalHostId, ConfigTomlAgentMode);
            for (int i = 0; i < SupportedReasoningEfforts.Length; i++)
                changed |= EnsureStringInArray(atoms, EnabledReasoningEffortsKey,
                    SupportedReasoningEfforts[i]);

            JavaScriptSerializer serializer = CreateSerializer();
            string json = serializer.Serialize(state);
            if (Encoding.UTF8.GetByteCount(json) > MaxGlobalStateBytes)
                throw new InvalidDataException("Codex global state is too large after onboarding suppression.");

            if (changed || !FileTextEquals(layout.GlobalStateFile, json))
                IOUtil.AtomicWriteText(layout.GlobalStateFile, json);
            if (changed || !FileTextEquals(layout.GlobalStateBackup, json))
                IOUtil.AtomicWriteText(layout.GlobalStateBackup, json);

            if (!IsSuppressed(layout))
                throw new InvalidDataException("Codex onboarding suppression state failed verification.");
        }

        internal static bool IsSuppressed(PortableLayout layout)
        {
            try
            {
                if (!File.Exists(layout.GlobalStateFile)) return false;
                Dictionary<string, object> state = ReadObject(layout.GlobalStateFile);
                object value;
                if (!state.TryGetValue(PersistedAtomStateKey, out value)) return false;
                Dictionary<string, object> atoms = value as Dictionary<string, object>;
                if (atoms == null) return false;
                object overrideValue;
                object completedValue;
                object pendingValue;
                object latestModel;
                object announcementValue;
                object agentModeValue;
                Dictionary<string, object> agentModes;
                return atoms.TryGetValue(OnboardingOverrideKey, out overrideValue) &&
                    string.Equals(overrideValue as string, "app", StringComparison.Ordinal) &&
                    atoms.TryGetValue(ProjectlessCompletedKey, out completedValue) &&
                    completedValue is bool && (bool)completedValue &&
                    atoms.TryGetValue(WelcomePendingKey, out pendingValue) &&
                    pendingValue is bool && !(bool)pendingValue &&
                    atoms.TryGetValue(KnowledgeWorkAnnouncementKey, out announcementValue) &&
                    announcementValue is bool && (bool)announcementValue &&
                    atoms.TryGetValue(FastModeAnnouncementKey, out announcementValue) &&
                    announcementValue is bool && (bool)announcementValue &&
                    atoms.TryGetValue(WorkPluginsAnnouncementKey, out announcementValue) &&
                    announcementValue is bool && (bool)announcementValue &&
                    atoms.TryGetValue(WalletAnnouncementKey, out announcementValue) &&
                    announcementValue is bool && (bool)announcementValue &&
                    ContainsStringInArray(atoms, SeenModelUpgradeListKey, CurrentModelUpgrade) &&
                    ContainsStringInArray(atoms, SeenModelUpgradeListKey,
                        OfficialAnnouncedModelUpgrade) &&
                    atoms.TryGetValue(LatestModelSeenKey, out latestModel) && latestModel == null &&
                    atoms.TryGetValue(AgentModeByHostIdKey, out agentModeValue) &&
                    (agentModes = agentModeValue as Dictionary<string, object>) != null &&
                    agentModes.TryGetValue(LocalHostId, out agentModeValue) &&
                    string.Equals(agentModeValue as string, ConfigTomlAgentMode,
                        StringComparison.Ordinal) &&
                    ContainsAllStringsInArray(atoms, EnabledReasoningEffortsKey,
                        SupportedReasoningEfforts);
            }
            catch { return false; }
        }

        private static Dictionary<string, object> ReadWithBackup(string primary, string backup)
        {
            Exception primaryError = null;
            if (File.Exists(primary))
            {
                try { return ReadObject(primary); }
                catch (Exception ex) { primaryError = ex; }
            }
            if (File.Exists(backup))
            {
                try { return ReadObject(backup); }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Codex global state and backup are invalid.",
                        primaryError ?? ex);
                }
            }
            if (primaryError != null)
                throw new InvalidDataException("Codex global state is invalid and has no usable backup.",
                    primaryError);
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private static Dictionary<string, object> ReadObject(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxGlobalStateBytes)
                throw new InvalidDataException("Codex global state size is invalid.");
            object parsed = CreateSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            Dictionary<string, object> result = parsed as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("Codex global state is not a JSON object.");
            return result;
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxGlobalStateBytes;
            return serializer;
        }

        private static Dictionary<string, object> GetOrCreateObject(
            Dictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                Dictionary<string, object> created =
                    new Dictionary<string, object>(StringComparer.Ordinal);
                parent[key] = created;
                return created;
            }
            Dictionary<string, object> existing = value as Dictionary<string, object>;
            if (existing == null)
                throw new InvalidDataException("Codex persisted atom state is not a JSON object.");
            return existing;
        }

        private static bool SetIfDifferent(Dictionary<string, object> values, string key,
            object expected)
        {
            object current;
            if (values.TryGetValue(key, out current) && object.Equals(current, expected)) return false;
            values[key] = expected;
            return true;
        }

        private static bool EnsureStringInArray(Dictionary<string, object> values, string key,
            string expected)
        {
            object current;
            List<object> items = new List<object>();
            if (values.TryGetValue(key, out current) && current != null)
            {
                IEnumerable enumerable = current as IEnumerable;
                if (enumerable == null || current is string)
                    throw new InvalidDataException("Codex persisted atom array has an invalid type: " + key);
                foreach (object item in enumerable)
                {
                    items.Add(item);
                    if (string.Equals(item as string, expected, StringComparison.Ordinal))
                        return false;
                }
            }
            items.Add(expected);
            values[key] = items.ToArray();
            return true;
        }

        private static bool ContainsStringInArray(Dictionary<string, object> values, string key,
            string expected)
        {
            object current;
            if (!values.TryGetValue(key, out current) || current == null || current is string)
                return false;
            IEnumerable enumerable = current as IEnumerable;
            if (enumerable == null) return false;
            foreach (object item in enumerable)
                if (string.Equals(item as string, expected, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool ContainsAllStringsInArray(Dictionary<string, object> values,
            string key, string[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (!ContainsStringInArray(values, key, expected[i])) return false;
            return true;
        }

        private static bool FileTextEquals(string path, string expected)
        {
            return File.Exists(path) &&
                string.Equals(File.ReadAllText(path, Encoding.UTF8), expected,
                    StringComparison.Ordinal);
        }
    }

    internal static class PortableBranding
    {
        internal const string DesktopExecutableName = "CodexDesktop.exe";
        internal const string AppUserModelId = "OpenAI.Codex.USB";
        private const string DarkIconResource = "CodexPortable.Branding.TrayDark.ico";
        private const string LightIconResource = "CodexPortable.Branding.TrayLight.ico";

        internal static void InitializeProcessIdentity()
        {
            try { NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
            catch { }
        }

        internal static Icon LoadLauncherIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                if (icon != null) return icon;
            }
            catch { }
            return (Icon)SystemIcons.Application.Clone();
        }

        internal static void EnsurePortablePayload(PortableLayout layout)
        {
            // First-launch package activation prepares and verifies the payload
            // from the USB-resident MSIX. Later starts only verify the installed
            // tree and never rewrite the 200+ MiB ASAR.
            string official = layout.OfficialAppExe;
            string alias = layout.AppExe;
            string resources = layout.Resources;
            if (!File.Exists(official))
                throw new FileNotFoundException("Official Codex Desktop payload is missing.", official);
            if (!File.Exists(alias))
                throw new FileNotFoundException("Prepared Codex Desktop executable is missing.", alias);
            if (!Directory.Exists(resources))
                throw new DirectoryNotFoundException("Codex Desktop resources are missing.");
            string[] requiredFiles = new string[] {
                Path.Combine(resources, "app.asar"),
                Path.Combine(resources, "codex-tray.ico"),
                Path.Combine(resources, "chatgpt-tray-dark.ico"),
                Path.Combine(resources, "chatgpt-tray-light.ico"),
                Path.Combine(resources, "icon-chatgpt.ico"),
                Path.Combine(resources, "icon.ico"),
                Path.Combine(resources, "owl-electron-app.json")
            };
            for (int i = 0; i < requiredFiles.Length; i++)
                if (!File.Exists(requiredFiles[i]))
                    throw new FileNotFoundException("Prepared portable branding file is missing.", requiredFiles[i]);
            if (!IsPrepared(layout))
                throw new InvalidDataException("Existing Codex Desktop payload is not an LF-prepared, updater-disabled payload.");
        }

        internal static void PreparePayload(string payloadRoot)
        {
            string official = Path.Combine(payloadRoot, "ChatGPT.exe");
            if (!File.Exists(official)) throw new FileNotFoundException("Official Codex Desktop payload is missing.", official);

            string resources = Path.Combine(payloadRoot, "resources");
            if (!Directory.Exists(resources)) throw new DirectoryNotFoundException("Codex Desktop resources are missing.");
            InstallEmbeddedIcon(DarkIconResource, Path.Combine(resources, "codex-tray.ico"));
            InstallEmbeddedIcon(DarkIconResource, Path.Combine(resources, "chatgpt-tray-dark.ico"));
            InstallEmbeddedIcon(LightIconResource, Path.Combine(resources, "chatgpt-tray-light.ico"));
            InstallEmbeddedIcon(DarkIconResource, Path.Combine(resources, "icon-chatgpt.ico"));
            InstallEmbeddedIcon(DarkIconResource, Path.Combine(resources, "icon.ico"));
            string asarPath = Path.Combine(resources, "app.asar");
            string oldHeaderHash = AsarPortableBranding.ComputeAsarHeaderHash(asarPath);
            AsarPortableBranding.EnsurePatched(asarPath);
            string newHeaderHash = AsarPortableBranding.ComputeAsarHeaderHash(asarPath);
            // Keep the desktop replica byte-identical to the official-named
            // executable while both follow the rewritten asar header hash.
            AsarPortableBranding.SyncExecutableAsarHeaderHash(official,
                oldHeaderHash, newHeaderHash);
            string alias = Path.Combine(payloadRoot, DesktopExecutableName);
            EnsureByteIdenticalCopy(official, alias);
            PrepareOwlMetadata(Path.Combine(resources, "owl-electron-app.json"));
        }

        internal static bool IsPrepared(PortableLayout layout)
        {
            return IsPrepared(layout.CurrentApp);
        }

        internal static bool IsPrepared(string payloadRoot)
        {
            return HasPreparedPayloadState(payloadRoot);
        }

        private static bool HasPreparedPayloadState(string payloadRoot)
        {
            try
            {
                if (!FilesEqual(Path.Combine(payloadRoot, "ChatGPT.exe"),
                    Path.Combine(payloadRoot, DesktopExecutableName))) return false;
                string resources = Path.Combine(payloadRoot, "resources");
                byte[] dark = ReadEmbeddedResource(DarkIconResource);
                byte[] light = ReadEmbeddedResource(LightIconResource);
                try
                {
                    return FileEqualsBytes(Path.Combine(resources, "codex-tray.ico"), dark) &&
                        FileEqualsBytes(Path.Combine(resources, "chatgpt-tray-dark.ico"), dark) &&
                        FileEqualsBytes(Path.Combine(resources, "chatgpt-tray-light.ico"), light) &&
                        FileEqualsBytes(Path.Combine(resources, "icon-chatgpt.ico"), dark) &&
                        FileEqualsBytes(Path.Combine(resources, "icon.ico"), dark) &&
                        AsarPortableBranding.IsPrepared(Path.Combine(resources, "app.asar")) &&
                        IsOwlMetadataPrepared(Path.Combine(resources, "owl-electron-app.json"));
                }
                finally
                {
                    Array.Clear(dark, 0, dark.Length);
                    Array.Clear(light, 0, light.Length);
                }
            }
            catch { return false; }
        }

        private static void PrepareOwlMetadata(string path)
        {
            string json = "{\"packagedFrom\":\"portable-release\",\"runtimeName\":\"owl\"}\r\n";
            IOUtil.AtomicWriteText(path, json);
            if (!IsOwlMetadataPrepared(path))
                throw new InvalidDataException("Portable desktop metadata verification failed.");
        }

        private static bool IsOwlMetadataPrepared(string path)
        {
            try
            {
                Dictionary<string, object> metadata = ReadOwlMetadata(path);
                object packagedFrom;
                object runtimeName;
                return metadata.Count == 2 &&
                    metadata.TryGetValue("packagedFrom", out packagedFrom) &&
                    string.Equals(packagedFrom as string, "portable-release", StringComparison.Ordinal) &&
                    metadata.TryGetValue("runtimeName", out runtimeName) &&
                    string.Equals(runtimeName as string, "owl", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static Dictionary<string, object> ReadOwlMetadata(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Desktop runtime metadata is missing.", path);
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 1024 * 1024)
                throw new InvalidDataException("Desktop runtime metadata size is invalid.");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 1024 * 1024;
            Dictionary<string, object> metadata = serializer.DeserializeObject(
                File.ReadAllText(path, new UTF8Encoding(false, true))) as Dictionary<string, object>;
            if (metadata == null) throw new InvalidDataException("Desktop runtime metadata is not a JSON object.");
            return metadata;
        }

        private static void EnsureByteIdenticalCopy(string source, string target)
        {
            if (FilesEqual(source, target)) return;
            byte[] bytes = File.ReadAllBytes(source);
            try
            {
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                IOUtil.AtomicWriteBytes(target, bytes);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
            if (!FilesEqual(source, target)) throw new IOException("Codex-named desktop payload verification failed.");
        }

        private static void InstallEmbeddedIcon(string resourceName, string target)
        {
            byte[] bytes = ReadEmbeddedResource(resourceName);
            try
            {
                if (FileEqualsBytes(target, bytes)) return;
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                IOUtil.AtomicWriteBytes(target, bytes);
                if (!FileEqualsBytes(target, bytes)) throw new IOException("Portable icon verification failed.");
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private static byte[] ReadEmbeddedResource(string resourceName)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null || stream.Length <= 0 || stream.Length > 1024 * 1024)
                    throw new InvalidDataException("Portable icon resource is missing or invalid.");
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException("Portable icon resource is truncated.");
                    offset += count;
                }
                return bytes;
            }
        }

        private static bool FilesEqual(string first, string second)
        {
            if (!File.Exists(first) || !File.Exists(second)) return false;
            FileInfo a = new FileInfo(first);
            FileInfo b = new FileInfo(second);
            if (a.Length != b.Length) return false;
            using (FileStream x = File.OpenRead(first))
            using (FileStream y = File.OpenRead(second)) return StreamsEqual(x, y);
        }

        private static bool FileEqualsBytes(string path, byte[] expected)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != expected.Length) return false;
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] buffer = new byte[8192];
                int offset = 0;
                try
                {
                    while (offset < expected.Length)
                    {
                        int count = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
                        if (count == 0) return false;
                        for (int i = 0; i < count; i++) if (buffer[i] != expected[offset + i]) return false;
                        offset += count;
                    }
                    return stream.ReadByte() == -1;
                }
                finally { Array.Clear(buffer, 0, buffer.Length); }
            }
        }

        private static bool StreamsEqual(Stream first, Stream second)
        {
            byte[] a = new byte[64 * 1024];
            byte[] b = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    int countA = first.Read(a, 0, a.Length);
                    int countB = second.Read(b, 0, b.Length);
                    if (countA != countB) return false;
                    if (countA == 0) return true;
                    for (int i = 0; i < countA; i++) if (a[i] != b[i]) return false;
                }
            }
            finally
            {
                Array.Clear(a, 0, a.Length);
                Array.Clear(b, 0, b.Length);
            }
        }
    }

    internal static class AsarPortableBranding
    {
        private const string BuildJavaScriptPrefix = ".vite/build/";
        // The desktop bundle centralizes every Windows/Store/Sparkle updater
        // decision in this environment gate. Keep the replacement the same
        // byte length so the ASAR data offsets and integrity header remain
        // stable while the portable payload fails closed.
        private const string OfficialSparkleGateText =
            "p=e=>e.CODEX_SPARKLE_ENABLED===`false`";
        private static readonly string PortableSparkleGateText =
            "p=e=>!0".PadRight(OfficialSparkleGateText.Length);
        private const string OfficialWorkerSparkleGateText =
            "Ege=e=>e.CODEX_SPARKLE_ENABLED===`false`";
        private static readonly string PortableWorkerSparkleGateText =
            "Ege=e=>!0".PadRight(OfficialWorkerSparkleGateText.Length);
        private const string OfficialUpdateMenuHandlerText =
            "enabled:!0,click:()=>{v7().info(`Check for updates requested via menu.`),d.checkForUpdates().then(()=>{if(d.hasUpdater())return;let e=d.getUnavailableReason()??`unknown`;v7().warning(`Desktop updater unavailable; init likely skipped.`,{safe:{reason:e},sensitive:{}}),l.dialog.showMessageBox({type:`info`,title:`Updates Unavailable`,message:`Automatic updates are unavailable right now.`,detail:`Updater initialization skipped: ${e}`})})}";
        private static readonly string PortableUpdateMenuHandlerText =
            "visible:!1,click:()=>{}                                                                                                                                                                                                                                                                                                                                                                                                                              ".PadRight(OfficialUpdateMenuHandlerText.Length);
        private const string OfficialUpdaterIdleStateText =
            "updateLifecycleState=`idle`";
        private const string PortableUpdaterIdleStateText =
            "updateLifecycleState=`none`";
        // Workspace dependencies have their own updater, independent of the
        // desktop Sparkle/Windows updater.  It polls the configured runtime
        // release and can rewrite the plugin marketplace/cache in the middle
        // of a portable session.  Keep the byte-preserving replacements tied
        // to the exact upstream implementation so an unrecognized bundle
        // fails closed instead of being partially patched.
        private const string OfficialRuntimeStaticDisabledReasonText =
            "getStaticDisabledReason(){return this.options.hostId===`local`?this.options.sharedObjectRepository?.get(`codex_runtimes_config`)==null?`runtime-config-missing`:e2(this.options.sharedObjectRepository?.get(`statsig_default_enable_features`))?null:`feature-gate-disabled`:`not-local-host`}";
        private static readonly string PortableRuntimeStaticDisabledReasonText =
            "getStaticDisabledReason(){return`portable-runtime-updates-disabled`                                                                                                                                                                                                                          }".
                PadRight(OfficialRuntimeStaticDisabledReasonText.Length);
        private const string OfficialRuntimeInstallGuardText =
            "async#e(e){if(!await this.isWorkspaceDependenciesFeatureEnabled(e))throw Error(`Codex dependencies are disabled in settings.`)}";
        private static readonly string PortableRuntimeInstallGuardText =
            "async#e(e){throw Error(`portable-runtime-updates-disabled`)}".
                PadRight(OfficialRuntimeInstallGuardText.Length);
        private const string WorkspaceDependenciesSettingsFunctionText =
            "function pr(e){return e.name===rt}";
        private const string OfficialWorkspaceDependenciesSettingsPanelGateText =
            "o&&n.kind===`local`?(0,$.jsx)(dr,{hostId:t}):null";
        private const string PortableWorkspaceDependenciesSettingsPanelGateText =
            "0&&n.kind===`local`?(0,$.jsx)(dr,{hostId:t}):null";
        // Codex normally collapses a config.toml permission pair into a built-in
        // mode when their effective permissions are identical. LF keeps the
        // config-backed mode explicit so the UI and the execution source agree.
        private const string OfficialConfigModeEquivalenceText =
            "y=v?`guardian-approvals`:g";
        private static readonly string PortableConfigModeEquivalenceText =
            "y=null".PadRight(OfficialConfigModeEquivalenceText.Length);
        private const string OfficialConfigModeShortLabelText =
            "id:`composer.permissionsDropdown.custom.shortLabel`,defaultMessage:`Custom`,description:`Short trigger label for the custom approvals mode`";
        private static readonly string PortableConfigModeShortLabelText =
            "id:`composer.permissionsDropdown.custom.configToml`,defaultMessage:`config.toml`,description:`Trigger label for the custom approvals mode`".
                PadRight(OfficialConfigModeShortLabelText.Length);
        private const string OfficialConfigModeOptionLabelText =
            "id:`composer.permissionsDropdown.custom.optionLabel`,defaultMessage:`Custom (config.toml)`,description:`Dropdown option for the custom permissions mode`";
        private static readonly string PortableConfigModeOptionLabelText =
            "id:`composer.permissionsDropdown.custom.configLabel`,defaultMessage:`config.toml`,description:`Dropdown option for the custom permissions mode`".
                PadRight(OfficialConfigModeOptionLabelText.Length);
        // The current official bundle renders both labels in the picker and
        // in the composer control.  Every rendering must identify config.toml
        // as its source; a changed count is an upstream compatibility break.
        private const int ConfigModeShortLabelExpectedOccurrences = 2;
        private const int ConfigModeOptionLabelExpectedOccurrences = 2;
        // The desktop evaluates these bundled tools in both WebView eligibility
        // and the main-process reconciler. LF supplies the same local runtimes
        // without ChatGPT authentication, so both gates must keep the three
        // plugins available and let their local capability checks decide at use time.
        private const string OfficialBrowserPluginAvailabilityText =
            "function cIr({areRequirementsPending:e,isBrowserAgentGateEnabled:t,isBrowserAndComputerUseAllowed:n,isBrowserEnabled:r,isBrowserUseEnabled:i,isLoading:a,runCodexInWsl:o,windowType:s}){return s===`chrome-extension`?`window-type-disabled`:e?`loading`:n?a?`loading`:r?t?i?o?`wsl-disabled`:`available`:`config-requirement-disabled`:`statsig-disabled`:`browser-pane-disabled`:`config-requirement-disabled`}";
        private static readonly string PortableBrowserPluginAvailabilityText =
            "function cIr({areRequirementsPending:e,isBrowserAgentGateEnabled:t,isBrowserAndComputerUseAllowed:n,isBrowserEnabled:r,isBrowserUseEnabled:i,isLoading:a,runCodexInWsl:o,windowType:s}){return`available`                                                                                                                                                                                                       }".
                PadRight(OfficialBrowserPluginAvailabilityText.Length);
        private const string OfficialChromePluginAvailabilityText =
            "function iIr({areRequirementsPending:e,isBrowserAndComputerUseAllowed:t,isExternalBrowserUseFeatureEnabled:n,isExternalBrowserUseFeatureLoading:r,isExternalBrowserUseGateEnabled:i,runCodexInWsl:a,windowType:o}){return e?`loading`:t?o===`chrome-extension`?`available`:r?`loading`:i?n?a?`wsl-disabled`:`available`:`config-requirement-disabled`:`statsig-disabled`:`config-requirement-disabled`}";
        private static readonly string PortableChromePluginAvailabilityText =
            "function iIr({areRequirementsPending:e,isBrowserAndComputerUseAllowed:t,isExternalBrowserUseFeatureEnabled:n,isExternalBrowserUseFeatureLoading:r,isExternalBrowserUseGateEnabled:i,runCodexInWsl:a,windowType:o}){return`available`                                                                                                                                                                  }".
                PadRight(OfficialChromePluginAvailabilityText.Length);
        private const string OfficialComputerUsePluginAvailabilityText =
            "function $Fr({areRequirementsPending:e,areRequiredFeaturesEnabled:t,enabled:n,isBrowserAndComputerUseAllowed:r,isAnyFeatureLoading:i,isComputerUseGateEnabled:a,isHostCompatiblePlatform:o,isPlatformLoading:s,windowType:c}){return n?c===`electron`?e?`loading`:r?a?s?`loading`:o?i?`loading`:t?`available`:`config-requirement-disabled`:`unsupported-platform`:`statsig-disabled`:`config-requirement-disabled`:`window-type-disabled`:`disabled`}";
        private static readonly string PortableComputerUsePluginAvailabilityText =
            "function $Fr({areRequirementsPending:e,areRequiredFeaturesEnabled:t,enabled:n,isBrowserAndComputerUseAllowed:r,isAnyFeatureLoading:i,isComputerUseGateEnabled:a,isHostCompatiblePlatform:o,isPlatformLoading:s,windowType:c}){return`available`                                                                                                                                                                                                      }".
                PadRight(OfficialComputerUsePluginAvailabilityText.Length);
        private const string OfficialBrowserPluginReconcileAvailabilityText =
            "{...n.Gs.browser,autoInstallOptOutKey:n.Xs(n.Gs.browser.name),isAvailable:({features:e})=>e.inAppBrowserUseAllowed||e.externalBrowserUseAllowed,migrate:vne}";
        private static readonly string PortableBrowserPluginReconcileAvailabilityText =
            "{...n.Gs.browser,autoInstallOptOutKey:n.Xs(n.Gs.browser.name),isAvailable:()=>!0,migrate:vne}".
                PadRight(OfficialBrowserPluginReconcileAvailabilityText.Length);
        private const string OfficialChromePluginReconcileAvailabilityText =
            "{...n.Gs.chrome,syncInstallStateWithChromeExtension:!0,isAvailable:({buildFlavor:e,features:t})=>t.externalBrowserUseAllowed&&s.p(e)}";
        private static readonly string PortableChromePluginReconcileAvailabilityText =
            "{...n.Gs.chrome,syncInstallStateWithChromeExtension:!0,isAvailable:()=>!0}".
                PadRight(OfficialChromePluginReconcileAvailabilityText.Length);
        private const string OfficialComputerUsePluginReconcileAvailabilityText =
            "{...n.Gs.computerUse,autoInstallOptOutKey:n.Xs(n.Gs.computerUse.name),isAvailable:({features:e,platform:t})=>t===`win32`&&e.computerUse}";
        private static readonly string PortableComputerUsePluginReconcileAvailabilityText =
            "{...n.Gs.computerUse,autoInstallOptOutKey:n.Xs(n.Gs.computerUse.name),isAvailable:()=>!0}".
                PadRight(OfficialComputerUsePluginReconcileAvailabilityText.Length);
        // The signed desktop bundle also reconciles the Sites and Deep Research
        // plugins against feature flags.  LF ships these plugins locally and
        // must keep their verified cache materialized even when the host has no
        // account-backed feature flags; otherwise the first desktop start
        // removes required cache trees immediately after the
        // launcher repairs them.
        private const string OfficialSitesPluginReconcileAvailabilityText =
            "{...n.Gs.sites,autoInstallOptOutKey:n.Xs(n.Gs.sites.name),syncToRemoteSshHosts:!0,isAvailable:({features:e})=>e.sites}";
        private static readonly string PortableSitesPluginReconcileAvailabilityText =
            "{...n.Gs.sites,autoInstallOptOutKey:n.Xs(n.Gs.sites.name),syncToRemoteSshHosts:!0,isAvailable:()=>!0}".
                PadRight(OfficialSitesPluginReconcileAvailabilityText.Length);
        private const string OfficialDeepResearchPluginReconcileAvailabilityText =
            "{...n.Gs.deepResearch,isAvailable:({features:e})=>e.deepResearch}";
        private static readonly string PortableDeepResearchPluginReconcileAvailabilityText =
            "{...n.Gs.deepResearch,isAvailable:()=>!0}".
                PadRight(OfficialDeepResearchPluginReconcileAvailabilityText.Length);
        private const string OfficialSunsetUpdateGateText = "if(FE(`2929582856`)){";
        private static readonly string PortableSunsetUpdateGateText =
            "if(!1".PadRight(OfficialSunsetUpdateGateText.Length - 2) + "){";
        // The current bundle resolves the Linux/package brand default in the
        // shared build module instead of exposing codexAppBrand in package.json.
        private const string OfficialBrandText =
            "codexAppBrand:n.Ml().trim().pipe(n.Tl(`chatgpt`))";
        private static readonly string PortableBrandText =
            "codexAppBrand:n.Ml().trim().pipe(n.Tl(`codex  `))".
                PadRight(OfficialBrandText.Length);
        private const string OfficialAumidText = "Prod:return`com.openai.codex`";
        private const string PortableAumidText = "Prod:return`OpenAI.Codex.USB`";
        private const string OfficialPortableUserDataResolverText =
            "function w({appDataPath:e,buildFlavor:n,env:r}){let i=r.CODEX_ELECTRON_USER_DATA_PATH?.trim();if(i)return(0,o.resolve)(i);let a=t.Ua(n),s=(0,o.join)(e,a==null?`Codex`:`Codex (${a})`),c=r.CODEX_ELECTRON_AGENT_RUN_ID?.trim()||null;return n===`agent`&&c!=null?(0,o.join)(s,`agent`,c):s}";
        private static readonly string PortableUserDataResolverText =
            "function w({env:r}){let i=r.CODEX_ELECTRON_USER_DATA_PATH?.trim();return i&&r.CODEX_PORTABLE_ROOT?(0,o.resolve)(i):(a.dialog.showErrorBox(`LF Portable`,`Open CodexPortable.exe from the USB drive.`),process.exit(1))}".
                PadRight(OfficialPortableUserDataResolverText.Length);
        private const string OfficialCloseToTrayText =
            "canHideLastWindowToTray?.()===!0&&!t){e.preventDefault(),P.hide();return}";
        private const string LegacyPortableCloseToTrayText =
            "canHideLastWindowToTray?.()&&!!0&&!t){e.preventDefault(),P.hide();return}";
        private const string PortableCloseToTrayText =
            "canHideLastWindowToTray?.()===!0&&!t){this.isAppQuitting=!0,l.app.quit()}";
        private const string PortableCloseElectronAliasText = "l=require(\"electron\")";
        private const string OfficialWindowsLastWindowText =
            "o.app.on(`window-all-closed`,()=>{process.platform!==`win32`";
        private const string PortableWindowsLastWindowText =
            "o.app.on(`window-all-closed`,()=>{process.platform===`win32`";
        private const string OfficialWindowsWindowIconSelectorText =
            "M=process.platform===`linux`?G5(t,T):j()";
        private const string PortableWindowsWindowIconSelectorText =
            "M=process.platform===`win32`?G5(t,T):j()";
        private const string OfficialWindowsWindowIconResolverText =
            "function G5(e,t=(0,p.join)(l.app.getAppPath(),`src`,`icons`)){let n=`${wS(e)}.png`;if(l.app.isPackaged){let e=(0,p.join)(process.resourcesPath,n);if((0,_.existsSync)(e))return e}let r=(0,p.join)(t,n);return(0,_.existsSync)(r)?r:null}";
        private static readonly string PortableWindowsWindowIconResolverText =
            "function G5(e,t=(0,p.join)(l.app.getAppPath(),`src`,`icons`)){let n=`${wS(e)}.ico`;if(l.app.isPackaged){let e=(0,p.join)(process.resourcesPath,n);if((0,_.existsSync)(e))return e}let r=(0,p.join)(t,n);return(0,_.existsSync)(r)?r:null}";
        private const string WebviewAssetPrefix = "webview/assets/";
        private const string AppInitialAssetStem = "app-initial";
        private const string OfficialStandardOnboardingGateText =
            "shouldShowStandardOnboarding:y";
        private const string PortableStandardOnboardingGateText =
            "shouldShowStandardOnboarding:0";
        // The model-upgrade surface is independent of the standard onboarding
        // gate. Patch its one final render guard rather than model names or
        // the shared announcement helper, so a new server-advertised model
        // cannot reintroduce the initial "Try model" CTA while unrelated NUX
        // surfaces keep their normal behavior.
        private const string OfficialTryModelAvailabilityGateText =
            "function Tcc(){let e=(0,Dcc.c)(3),{announcementContent:t,dismissAnnouncement:n,showAnnouncement:r}=x_a();if(!r||t==null)return null;";
        private const string PortableTryModelAvailabilityGateText =
            "function Tcc(){let e=(0,Dcc.c)(3),{announcementContent:t,dismissAnnouncement:n,showAnnouncement:r}=x_a();if(!0||t==null)return null;";
        private const string OfficialTryModelUpgradeGateText =
            "function Ecc(){let e=(0,Dcc.c)(10),t=A_($),{announcementContent:n,dismissAnnouncement:r,showAnnouncement:i}=S_a(),a=Z(Fcc)??wb(Ncc,!1),{serviceTierSettings:o}=jq(),s=o.selectedServiceTier!=null,{estimate:c,estimateStatus:l,isEstimateFreshForAnnouncement:u}=Aoc(!a&&i&&n!=null&&!s),d=!a&&i&&n!=null&&l===`ready`&&c!=null&&u,f,p;if(e[0]!==a||e[1]!==s||e[2]!==t?(f=()=>{!s||a||t.set(Fcc,!0)},p=[a,s,t],e[0]=a,e[1]=s,e[2]=t,e[3]=f,e[4]=p):(f=e[3],p=e[4]),(0,Occ.useEffect)(f,p),!d||n==null||c==null)return null;";
        private const string PortableTryModelUpgradeGateText =
            "function Ecc(){let e=(0,Dcc.c)(10),t=A_($),{announcementContent:n,dismissAnnouncement:r,showAnnouncement:i}=S_a(),a=Z(Fcc)??wb(Ncc,!1),{serviceTierSettings:o}=jq(),s=o.selectedServiceTier!=null,{estimate:c,estimateStatus:l,isEstimateFreshForAnnouncement:u}=Aoc(!a&&i&&n!=null&&!s),d=!a&&i&&n!=null&&l===`ready`&&c!=null&&u,f,p;if(e[0]!==a||e[1]!==s||e[2]!==t?(f=()=>{!s||a||t.set(Fcc,!0)},p=[a,s,t],e[0]=a,e[1]=s,e[2]=t,e[3]=f,e[4]=p):(f=e[3],p=e[4]),(0,Occ.useEffect)(f,p),!0||n==null||c==null)return null;";
        private const string OnboardingMessageIdPrefix =
            "electron.onboarding.conversationalOnboarding.";
        private const string OfficialOnboardingBrandText = "ChatGPT";
        private const string PortableOnboardingBrandText = "Codex";
        private const string OnboardingHeaderContainerClassText =
            "className:`fixed inset-x-0 top-0 z-10 flex h-toolbar items-center justify-center bg-surface draggable select-none`";
        private const string OnboardingHeaderIconClassText =
            "className:`pointer-events-none size-6 text-default`";
        private const string OfficialWindowsSandboxSetupPendingGateText =
            "isWindowsSandboxSetupPending:Sr!=null&&Ct";
        private static readonly string PortableWindowsSandboxSetupPendingGateText =
            "isWindowsSandboxSetupPending:!1".
                PadRight(OfficialWindowsSandboxSetupPendingGateText.Length);
        // LF's config.toml can explicitly select danger-full-access, which does
        // not use the one-time Windows Agent sandbox. The current desktop maps
        // every non-read-only custom mode to that host setup anyway. Clear only
        // the derived composer state so the configured Codex permissions remain
        // authoritative without introducing a UAC or fixed-disk prerequisite.
        private const string OfficialWindowsSandboxComposerStateText =
            "function ywo({allowElevatedSetup:e,allowUnelevatedFallback:t,hasReadinessError:n,isSetupModePending:r,onboardingDismissed:i,phase:a,requiresSetup:o}){return i?`none`:n?`show`:o?r?`waitForPolicy`:!e&&t?a===`idle`?`startUnelevated`:`none`:`show`:`none`}";
        private static readonly string PortableWindowsSandboxComposerStateText =
            "function ywo({allowElevatedSetup:e,allowUnelevatedFallback:t,hasReadinessError:n,isSetupModePending:r,onboardingDismissed:i,phase:a,requiresSetup:o}){return`none`                                                                                        }".
                PadRight(OfficialWindowsSandboxComposerStateText.Length);
        // A readiness failure from the desktop app-server otherwise remains a
        // submit-blocking state even when LF has already selected the
        // config.toml danger-full-access mode.  Normalize the three fields in
        // the composer snapshot together: the requirement value, its error
        // flag, and the pending flag.  Keep this replacement byte-preserving
        // so the surrounding ASAR offsets and integrity metadata remain valid.
        private const string OfficialWindowsSandboxReadinessStateText =
            "windowsSandboxRequirement:Dr,hasWindowsSandboxRequirementError:Tr,isWindowsSandboxRequirementPending:Er";
        private static readonly string PortableWindowsSandboxReadinessStateText =
            "windowsSandboxRequirement:!1,hasWindowsSandboxRequirementError:!1,isWindowsSandboxRequirementPending:!1";
        private const string OfficialWindowsSandboxFinalStepText =
            "function hc(e,t){return e.finalStep.shouldShow&&!t?X.WindowsSandboxSetup:X.Complete}";
        private static readonly string PortableWindowsSandboxFinalStepText =
            "function hc(e,t){return X.Complete}".PadRight(OfficialWindowsSandboxFinalStepText.Length);
        // 26.901 split the sandbox status into a second module chain that the
        // older composer gate does not cover: wNr mounts the persistent
        // "Finish Windows setup to continue" status card (vNr -> hNr -> gNr)
        // whenever the composer banner is armed.  Keep that mount disabled so
        // the portable build never blocks the composer on Windows-setup
        // readiness that the launcher cannot satisfy unelevated.
        private const string OfficialWindowsSandboxBannerMountText =
            "!n&&i?(0,e7.jsx)(vNr,{cwd:a===`/`||o?null:a,requirement:s,setShowWindowsSandboxBanner:c}):null";
        private static readonly string PortableWindowsSandboxBannerMountText =
            "!0&&0?(0,e7.jsx)(vNr,{cwd:a===`/`||o?null:a,requirement:s,setShowWindowsSandboxBanner:c}):null";
        private const int OnboardingBrandPaddingLength = 2;
        // English defaults live in onboarding-page. Localized assets are
        // discovered from their message keys below so a new locale or a
        // changed bundler hash does not make the payload unpreparable.

        private sealed class IntegrityState
        {
            internal string Hash;
            internal readonly List<string> Blocks = new List<string>();
        }

        private sealed class AsarEntry
        {
            internal string Path;
            internal long Offset;
            internal int Size;
            internal string IntegrityHash;
            internal int BlockSize;
            internal readonly List<string> IntegrityBlocks = new List<string>();
            internal int IntegrityHashOffset = -1;
            internal readonly List<int> IntegrityBlockOffsets = new List<int>();
        }

        private sealed class JsonPropertySpan
        {
            internal string Name;
            internal int ValueStart;
            internal int ValueEnd;
        }

        private sealed class IntegrityLocation
        {
            internal string Path;
            internal string Hash;
            internal int HashOffset;
            internal readonly List<string> Blocks = new List<string>();
            internal readonly List<int> BlockOffsets = new List<int>();
        }

        // JavaScriptSerializer gives us the typed ASAR tree, but does not
        // expose source positions. This small JSON scanner records the exact
        // hash-string offsets while walking the same `files` hierarchy.
        private sealed class HeaderJsonScanner
        {
            private readonly string Text;
            private int Position;

            internal HeaderJsonScanner(string text) { Text = text ?? ""; }

            internal List<IntegrityLocation> Scan()
            {
                SkipWhitespace();
                List<JsonPropertySpan> root = ReadObject();
                JsonPropertySpan files = FindProperty(root, "files");
                if (files == null || !IsObject(files.ValueStart))
                    throw new InvalidDataException("Electron ASAR file table is missing.");
                SkipWhitespace();
                if (Position != Text.Length)
                    throw new InvalidDataException("Electron ASAR header has trailing JSON data.");
                List<IntegrityLocation> result = new List<IntegrityLocation>();
                ScanDirectory(files.ValueStart, "", result);
                return result;
            }

            private void ScanDirectory(int objectStart, string prefix,
                List<IntegrityLocation> result)
            {
                List<JsonPropertySpan> children = ReadObjectAt(objectStart);
                for (int i = 0; i < children.Count; i++)
                {
                    JsonPropertySpan child = children[i];
                    if (!IsObject(child.ValueStart)) continue;
                    string path = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                    List<JsonPropertySpan> properties = ReadObjectAt(child.ValueStart);
                    JsonPropertySpan files = FindProperty(properties, "files");
                    if (files != null)
                    {
                        if (!IsObject(files.ValueStart))
                            throw new InvalidDataException("Electron ASAR directory is invalid.");
                        ScanDirectory(files.ValueStart, path, result);
                        continue;
                    }
                    JsonPropertySpan integrity = FindProperty(properties, "integrity");
                    if (integrity != null && IsObject(integrity.ValueStart))
                        result.Add(ReadIntegrity(path, integrity.ValueStart));
                }
            }

            private IntegrityLocation ReadIntegrity(string path, int objectStart)
            {
                List<JsonPropertySpan> properties = ReadObjectAt(objectStart);
                JsonPropertySpan hash = FindProperty(properties, "hash");
                JsonPropertySpan blocks = FindProperty(properties, "blocks");
                if (hash == null || blocks == null || !IsString(hash.ValueStart) ||
                    !IsArray(blocks.ValueStart))
                    throw new InvalidDataException("Electron ASAR SHA-256 metadata is incomplete.");
                int hashOffset;
                int hashEnd;
                string hashValue = ReadStringAt(hash.ValueStart, out hashOffset, out hashEnd);
                if (hashEnd - hashOffset != hashValue.Length)
                    throw new InvalidDataException("Electron ASAR hash encoding is unsupported.");
                IntegrityLocation result = new IntegrityLocation {
                    Path = path,
                    Hash = NormalizeHash(hashValue),
                    HashOffset = hashOffset
                };
                ReadStringArrayAt(blocks.ValueStart, result);
                return result;
            }

            private void ReadStringArrayAt(int arrayStart, IntegrityLocation result)
            {
                Position = arrayStart;
                SkipWhitespace();
                Expect('[');
                SkipWhitespace();
                if (Peek() == ']') { Position++; return; }
                while (true)
                {
                    SkipWhitespace();
                    if (!IsString(Position))
                        throw new InvalidDataException("Electron ASAR block hashes are invalid.");
                    int offset;
                    int end;
                    string value = ReadString(out offset, out end);
                    if (end - offset != value.Length)
                        throw new InvalidDataException("Electron ASAR block hash encoding is unsupported.");
                    result.Blocks.Add(NormalizeHash(value));
                    result.BlockOffsets.Add(offset);
                    SkipWhitespace();
                    char separator = Peek();
                    if (separator == ']') { Position++; return; }
                    if (separator != ',')
                        throw new InvalidDataException("Electron ASAR block hash array is invalid.");
                    Position++;
                }
            }

            private List<JsonPropertySpan> ReadObjectAt(int start)
            {
                Position = start;
                return ReadObject();
            }

            private List<JsonPropertySpan> ReadObject()
            {
                SkipWhitespace();
                Expect('{');
                List<JsonPropertySpan> result = new List<JsonPropertySpan>();
                SkipWhitespace();
                if (Peek() == '}') { Position++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (!IsString(Position))
                        throw new InvalidDataException("Electron ASAR JSON property name is invalid.");
                    int ignoredStart;
                    int ignoredEnd;
                    string name = ReadString(out ignoredStart, out ignoredEnd);
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    int valueStart = Position;
                    SkipValue();
                    result.Add(new JsonPropertySpan {
                        Name = name,
                        ValueStart = valueStart,
                        ValueEnd = Position
                    });
                    SkipWhitespace();
                    char separator = Peek();
                    if (separator == '}') { Position++; return result; }
                    if (separator != ',')
                        throw new InvalidDataException("Electron ASAR JSON object is invalid.");
                    Position++;
                }
            }

            private void SkipValue()
            {
                SkipWhitespace();
                char value = Peek();
                if (value == '{') { ReadObject(); return; }
                if (value == '[') { SkipArray(); return; }
                if (value == '"')
                {
                    int ignoredStart;
                    int ignoredEnd;
                    ReadString(out ignoredStart, out ignoredEnd);
                    return;
                }
                int start = Position;
                while (Position < Text.Length && ",}] \t\r\n".IndexOf(Text[Position]) < 0)
                    Position++;
                if (Position == start)
                    throw new InvalidDataException("Electron ASAR JSON value is invalid.");
            }

            private void SkipArray()
            {
                Expect('[');
                SkipWhitespace();
                if (Peek() == ']') { Position++; return; }
                while (true)
                {
                    SkipValue();
                    SkipWhitespace();
                    char separator = Peek();
                    if (separator == ']') { Position++; return; }
                    if (separator != ',')
                        throw new InvalidDataException("Electron ASAR JSON array is invalid.");
                    Position++;
                }
            }

            private string ReadStringAt(int start, out int contentStart, out int contentEnd)
            {
                Position = start;
                return ReadString(out contentStart, out contentEnd);
            }

            private string ReadString(out int contentStart, out int contentEnd)
            {
                Expect('"');
                contentStart = Position;
                StringBuilder value = new StringBuilder();
                while (Position < Text.Length)
                {
                    char current = Text[Position++];
                    if (current == '"')
                    {
                        contentEnd = Position - 1;
                        return value.ToString();
                    }
                    if (current == '\\')
                    {
                        if (Position >= Text.Length)
                            throw new InvalidDataException("Electron ASAR JSON string is truncated.");
                        char escaped = Text[Position++];
                        switch (escaped)
                        {
                            case '"': value.Append('"'); break;
                            case '\\': value.Append('\\'); break;
                            case '/': value.Append('/'); break;
                            case 'b': value.Append('\b'); break;
                            case 'f': value.Append('\f'); break;
                            case 'n': value.Append('\n'); break;
                            case 'r': value.Append('\r'); break;
                            case 't': value.Append('\t'); break;
                            case 'u':
                                if (Position + 4 > Text.Length)
                                    throw new InvalidDataException("Electron ASAR JSON unicode escape is truncated.");
                                int code = 0;
                                for (int i = 0; i < 4; i++)
                                {
                                    int digit = HexValue(Text[Position + i]);
                                    if (digit < 0) throw new InvalidDataException("Electron ASAR JSON unicode escape is invalid.");
                                    code = code * 16 + digit;
                                }
                                value.Append((char)code);
                                Position += 4;
                                break;
                            default:
                                throw new InvalidDataException("Electron ASAR JSON escape is invalid.");
                        }
                    }
                    else
                    {
                        if (current < 0x20) throw new InvalidDataException("Electron ASAR JSON string contains a control character.");
                        value.Append(current);
                    }
                }
                throw new InvalidDataException("Electron ASAR JSON string is truncated.");
            }

            private static int HexValue(char value)
            {
                if (value >= '0' && value <= '9') return value - '0';
                if (value >= 'a' && value <= 'f') return value - 'a' + 10;
                if (value >= 'A' && value <= 'F') return value - 'A' + 10;
                return -1;
            }

            private static JsonPropertySpan FindProperty(List<JsonPropertySpan> properties,
                string name)
            {
                JsonPropertySpan result = null;
                for (int i = 0; i < properties.Count; i++)
                    if (string.Equals(properties[i].Name, name, StringComparison.Ordinal))
                    {
                        if (result != null)
                            throw new InvalidDataException("Electron ASAR JSON property is duplicated: " + name);
                        result = properties[i];
                    }
                return result;
            }

            private bool IsObject(int offset) { return offset >= 0 && offset < Text.Length && Text[offset] == '{'; }
            private bool IsArray(int offset) { return offset >= 0 && offset < Text.Length && Text[offset] == '['; }
            private bool IsString(int offset) { return offset >= 0 && offset < Text.Length && Text[offset] == '"'; }
            private char Peek() { return Position < Text.Length ? Text[Position] : '\0'; }

            private void SkipWhitespace()
            {
                while (Position < Text.Length && (Text[Position] == ' ' || Text[Position] == '\t' ||
                    Text[Position] == '\r' || Text[Position] == '\n')) Position++;
            }

            private void Expect(char expected)
            {
                if (Peek() != expected)
                    throw new InvalidDataException("Electron ASAR JSON structure is invalid.");
                Position++;
            }
        }

        private sealed class OnboardingEntryTarget
        {
            internal AsarEntry Entry;
            internal bool ContainsDefaultMessages;
        }

        private sealed class OnboardingLiteralTarget
        {
            internal bool IsPortable;
            internal int OpeningTickOffset;
            internal int BrandOffset;
        }

        private sealed class IdentifierPattern
        {
            internal Regex Regex;
            internal readonly Dictionary<string, string> Groups =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static readonly object IdentifierPatternCacheLock = new object();
        private static readonly Dictionary<string, IdentifierPattern> IdentifierPatternCache =
            new Dictionary<string, IdentifierPattern>(StringComparer.Ordinal);

        private sealed class AsarArchive : IDisposable
        {
            internal readonly FileStream Stream;
            private readonly string OriginalPath;
            private readonly string WorkingPath;
            private readonly bool Writable;
            private bool committed;
            private bool disposed;
            internal readonly long DataOffset;
            internal readonly int HeaderJsonLength;
            internal string HeaderJson;
            internal readonly List<AsarEntry> Entries = new List<AsarEntry>();
            // Header hashes are addressed by their JSON field offsets instead
            // of globally replacing hash text.  Unrelated ASAR entries may
            // legitimately share the same SHA-256 value.
            internal readonly Dictionary<int, string> IntegrityReplacements =
                new Dictionary<int, string>();

            internal AsarArchive(string path, bool writable)
            {
                OriginalPath = path;
                Writable = writable;
                if (writable)
                {
                    string directory = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(directory))
                        throw new InvalidDataException("Electron ASAR path has no parent directory.");
                    WorkingPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." +
                        Guid.NewGuid().ToString("N") + ".tmp");
                    File.Copy(path, WorkingPath, false);
                    File.SetAttributes(WorkingPath, FileAttributes.Normal);
                }
                else WorkingPath = path;
                Stream = new FileStream(WorkingPath, FileMode.Open,
                    writable ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
                byte[] prefix = new byte[16];
                ReadExact(Stream, prefix, 0, prefix.Length);
                uint sizePayloadLength = BitConverter.ToUInt32(prefix, 0);
                uint headerPickleLength = BitConverter.ToUInt32(prefix, 4);
                uint headerPayloadLength = BitConverter.ToUInt32(prefix, 8);
                uint headerJsonLength = BitConverter.ToUInt32(prefix, 12);
                if (sizePayloadLength != 4 || headerPickleLength < 8 ||
                    headerPayloadLength < 4 || headerJsonLength == 0 || headerJsonLength > 64 * 1024 * 1024 ||
                    16L + headerJsonLength > 8L + headerPickleLength)
                    throw new InvalidDataException("Unsupported Electron ASAR header.");

                DataOffset = 8L + headerPickleLength;
                HeaderJsonLength = checked((int)headerJsonLength);
                byte[] headerBytes = new byte[HeaderJsonLength];
                ReadExact(Stream, headerBytes, 0, headerBytes.Length);
                HeaderJson = new UTF8Encoding(false, true).GetString(headerBytes);

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                serializer.RecursionLimit = 512;
                Dictionary<string, object> header = serializer.Deserialize<Dictionary<string, object>>(HeaderJson);
                object filesObject;
                if (header == null || !header.TryGetValue("files", out filesObject))
                    throw new InvalidDataException("Electron ASAR file table is missing.");
                Dictionary<string, object> files = filesObject as Dictionary<string, object>;
                if (files == null) throw new InvalidDataException("Electron ASAR file table is invalid.");
                AddEntries(files, "");
                BindIntegrityOffsets();
            }

            private void AddEntries(Dictionary<string, object> files, string prefix)
            {
                foreach (KeyValuePair<string, object> pair in files)
                {
                    Dictionary<string, object> node = pair.Value as Dictionary<string, object>;
                    if (node == null) throw new InvalidDataException("Electron ASAR entry is invalid.");
                    string relative = prefix.Length == 0 ? pair.Key : prefix + "/" + pair.Key;
                    object childrenObject;
                    if (node.TryGetValue("files", out childrenObject))
                    {
                        Dictionary<string, object> children = childrenObject as Dictionary<string, object>;
                        if (children == null) throw new InvalidDataException("Electron ASAR directory is invalid.");
                        AddEntries(children, relative);
                        continue;
                    }

                    object offsetObject;
                    object sizeObject;
                    object integrityObject;
                    if (!node.TryGetValue("offset", out offsetObject) ||
                        !node.TryGetValue("size", out sizeObject) ||
                        !node.TryGetValue("integrity", out integrityObject)) continue;
                    long relativeOffset;
                    int size;
                    if (!long.TryParse(Convert.ToString(offsetObject, CultureInfo.InvariantCulture),
                            NumberStyles.None, CultureInfo.InvariantCulture, out relativeOffset) || relativeOffset < 0 ||
                        !int.TryParse(Convert.ToString(sizeObject, CultureInfo.InvariantCulture),
                            NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0)
                        throw new InvalidDataException("Electron ASAR entry bounds are invalid.");
                    if (DataOffset + relativeOffset < DataOffset || DataOffset + relativeOffset + size > Stream.Length)
                        throw new InvalidDataException("Electron ASAR entry exceeds the archive.");

                    Dictionary<string, object> integrity = integrityObject as Dictionary<string, object>;
                    if (integrity == null) throw new InvalidDataException("Electron ASAR integrity metadata is invalid.");
                    object algorithmObject;
                    object hashObject;
                    object blockSizeObject;
                    object blocksObject;
                    if (!integrity.TryGetValue("algorithm", out algorithmObject) ||
                        !string.Equals(Convert.ToString(algorithmObject, CultureInfo.InvariantCulture), "SHA256",
                            StringComparison.OrdinalIgnoreCase) ||
                        !integrity.TryGetValue("hash", out hashObject) ||
                        !integrity.TryGetValue("blockSize", out blockSizeObject) ||
                        !integrity.TryGetValue("blocks", out blocksObject))
                        throw new InvalidDataException("Electron ASAR SHA-256 metadata is incomplete.");

                    int blockSize;
                    if (!int.TryParse(Convert.ToString(blockSizeObject, CultureInfo.InvariantCulture),
                            NumberStyles.None, CultureInfo.InvariantCulture, out blockSize) || blockSize <= 0)
                        throw new InvalidDataException("Electron ASAR block size is invalid.");
                    AsarEntry entry = new AsarEntry();
                    entry.Path = relative;
                    entry.Offset = DataOffset + relativeOffset;
                    entry.Size = size;
                    entry.IntegrityHash = NormalizeHash(Convert.ToString(hashObject, CultureInfo.InvariantCulture));
                    entry.BlockSize = blockSize;
                    IEnumerable blocks = blocksObject as IEnumerable;
                    if (blocks == null) throw new InvalidDataException("Electron ASAR block hashes are invalid.");
                    foreach (object block in blocks)
                        entry.IntegrityBlocks.Add(NormalizeHash(Convert.ToString(block, CultureInfo.InvariantCulture)));
                    int expectedBlocks = size == 0 ? 1 : checked((int)(((long)size + blockSize - 1) / blockSize));
                    if (entry.IntegrityBlocks.Count != expectedBlocks)
                        throw new InvalidDataException("Electron ASAR block hash count is invalid.");
                    Entries.Add(entry);
                }
            }

            private void BindIntegrityOffsets()
            {
                HeaderJsonScanner scanner = new HeaderJsonScanner(HeaderJson);
                List<IntegrityLocation> locations = scanner.Scan();
                Dictionary<string, IntegrityLocation> byPath =
                    new Dictionary<string, IntegrityLocation>(StringComparer.Ordinal);
                for (int i = 0; i < locations.Count; i++)
                {
                    if (byPath.ContainsKey(locations[i].Path))
                        throw new InvalidDataException("Duplicate Electron ASAR integrity entry: " + locations[i].Path);
                    byPath.Add(locations[i].Path, locations[i]);
                }
                for (int i = 0; i < Entries.Count; i++)
                {
                    AsarEntry entry = Entries[i];
                    IntegrityLocation match;
                    if (!byPath.TryGetValue(entry.Path, out match) ||
                        !string.Equals(match.Hash, entry.IntegrityHash,
                            StringComparison.OrdinalIgnoreCase) ||
                        match.Blocks.Count != entry.IntegrityBlocks.Count ||
                        match.BlockOffsets.Count != entry.IntegrityBlocks.Count)
                        throw new InvalidDataException("Electron ASAR integrity field could not be located: " + entry.Path);
                    for (int block = 0; block < entry.IntegrityBlocks.Count; block++)
                        if (!string.Equals(match.Blocks[block], entry.IntegrityBlocks[block],
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Electron ASAR block integrity field could not be located: " + entry.Path);
                    entry.IntegrityHashOffset = match.HashOffset;
                    entry.IntegrityBlockOffsets.AddRange(match.BlockOffsets);
                }
            }

            internal AsarEntry FindRequiredEntry(string relativePath)
            {
                AsarEntry result = null;
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (!string.Equals(Entries[i].Path, relativePath, StringComparison.Ordinal)) continue;
                    if (result != null) throw new InvalidDataException("Duplicate Electron ASAR entry: " + relativePath);
                    result = Entries[i];
                }
                if (result == null) throw new InvalidDataException("Electron ASAR entry is missing: " + relativePath);
                return result;
            }

            internal byte[] ReadEntry(AsarEntry entry)
            {
                byte[] bytes = new byte[entry.Size];
                Stream.Position = entry.Offset;
                ReadExact(Stream, bytes, 0, bytes.Length);
                return bytes;
            }

            internal void WriteEntry(AsarEntry entry, byte[] bytes)
            {
                if (!Stream.CanWrite || bytes.Length != entry.Size)
                    throw new InvalidOperationException("Electron ASAR entry cannot be rewritten.");
                Stream.Position = entry.Offset;
                Stream.Write(bytes, 0, bytes.Length);
            }

            internal void AddIntegrityReplacement(AsarEntry entry, IntegrityState replacement)
            {
                if (entry.IntegrityHashOffset < 0 ||
                    entry.IntegrityBlockOffsets.Count != entry.IntegrityBlocks.Count)
                    throw new InvalidDataException("Electron ASAR integrity fields are not bound.");
                AddReplacement(entry.IntegrityHashOffset, replacement.Hash);
                if (entry.IntegrityBlocks.Count != replacement.Blocks.Count)
                    throw new InvalidDataException("Electron ASAR block hashes changed shape.");
                for (int i = 0; i < entry.IntegrityBlocks.Count; i++)
                    AddReplacement(entry.IntegrityBlockOffsets[i], replacement.Blocks[i]);
                // Keep the in-memory entry metadata in lockstep with the
                // bytes just written.  A minified bundle can contain several
                // targets in one entry; the next target must validate against
                // this newly patched integrity state before the header flush.
                entry.IntegrityHash = replacement.Hash;
                entry.IntegrityBlocks.Clear();
                entry.IntegrityBlocks.AddRange(replacement.Blocks);
            }

            private void AddReplacement(int offset, string replacement)
            {
                if (replacement == null || replacement.Length != 64 ||
                    offset < 0 || offset + replacement.Length > HeaderJson.Length)
                    throw new InvalidDataException("Electron ASAR integrity field offset is invalid.");
                // A single entry can be patched more than once before the
                // header is flushed. Replace the pending value at that exact
                // field offset so H0 -> H1 -> H2 remains valid.
                IntegrityReplacements[offset] = replacement;
            }

            internal void FlushHeader()
            {
                if (IntegrityReplacements.Count == 0) return;
                char[] rewritten = HeaderJson.ToCharArray();
                foreach (KeyValuePair<int, string> pair in IntegrityReplacements)
                {
                    if (pair.Key < 0 || pair.Key + pair.Value.Length > rewritten.Length)
                        throw new InvalidDataException("Electron ASAR integrity replacement offset is invalid.");
                    for (int i = 0; i < pair.Value.Length; i++)
                        rewritten[pair.Key + i] = pair.Value[i];
                }
                string value = new string(rewritten);
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
                if (bytes.Length != HeaderJsonLength)
                    throw new InvalidDataException("Electron ASAR header length changed unexpectedly.");
                Stream.Position = 16;
                Stream.Write(bytes, 0, bytes.Length);
                Stream.Flush(true);
                HeaderJson = value;
                IntegrityReplacements.Clear();
            }

            internal void Commit()
            {
                if (!Writable || committed) return;
                FlushHeader();
                Stream.Flush(true);
                Stream.Dispose();
                IOUtil.AtomicReplaceFile(WorkingPath, OriginalPath);
                committed = true;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Stream.Dispose();
                if (Writable && !committed) IOUtil.TryDelete(WorkingPath);
            }
        }

        internal static string ComputeAsarHeaderHash(string asarPath)
        {
            byte[] prefix = new byte[16];
            using (FileStream stream = new FileStream(asarPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
            {
                int offset = 0;
                while (offset < prefix.Length)
                {
                    int read = stream.Read(prefix, offset, prefix.Length - offset);
                    if (read == 0)
                        throw new InvalidDataException("Electron ASAR header is truncated.");
                    offset += read;
                }
            }
            uint headerJsonLength = BitConverter.ToUInt32(prefix, 12);
            if (headerJsonLength == 0 || headerJsonLength > 64 * 1024 * 1024)
                throw new InvalidDataException("Electron ASAR header length is invalid.");
            byte[] headerBytes = new byte[headerJsonLength];
            using (FileStream stream = new FileStream(asarPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
            {
                stream.Position = 16;
                int offset = 0;
                while (offset < headerBytes.Length)
                {
                    int read = stream.Read(headerBytes, offset, headerBytes.Length - offset);
                    if (read == 0)
                        throw new InvalidDataException("Electron ASAR header is truncated.");
                    offset += read;
                }
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(headerBytes);
                StringBuilder builder = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++)
                    builder.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        internal static void SyncExecutableAsarHeaderHash(string executablePath,
            string oldHeaderHash, string newHeaderHash)
        {
            // Codex Desktop 26.901 validates the app.asar header against the
            // hash embedded in its own executable (Electron asar integrity
            // fuse).  LF rewrites app.asar with byte-preserving patches, so
            // the embedded expected hash must follow the rewritten header.
            // Executables without an embedded header hash (older payloads)
            // are left untouched.
            if (string.IsNullOrEmpty(oldHeaderHash) || string.IsNullOrEmpty(newHeaderHash) ||
                string.Equals(oldHeaderHash, newHeaderHash, StringComparison.Ordinal) ||
                !File.Exists(executablePath)) return;
            byte[] executable = File.ReadAllBytes(executablePath);
            byte[] oldBytes = Encoding.ASCII.GetBytes(oldHeaderHash);
            byte[] newBytes = Encoding.ASCII.GetBytes(newHeaderHash);
            if (newBytes.Length != 64) return;
            int found = 0;
            int index = 0;
            while ((index = IndexOfBytes(executable, oldBytes, index)) >= 0)
            {
                index += oldBytes.Length;
                found++;
            }
            if (found != 1)
            {
                // A missing token means this executable carries no asar header
                // expectation; tolerate it.  More than one identical token
                // would be ambiguous, so do not guess.
                return;
            }
            index = IndexOfBytes(executable, oldBytes, 0);
            byte[] replacement = (byte[])executable.Clone();
            Array.Copy(newBytes, 0, replacement, index, newBytes.Length);
            IOUtil.AtomicWriteBytes(executablePath, replacement);
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            if (needle.Length == 0 || start < 0) return -1;
            int limit = haystack.Length - needle.Length;
            for (int i = start; i <= limit; i++)
            {
                bool equal = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { equal = false; break; }
                if (equal) return i;
            }
            return -1;
        }

        internal static void EnsurePatched(string asarPath)
        {
            if (!File.Exists(asarPath)) throw new FileNotFoundException("Electron app.asar is missing.", asarPath);
            FileAttributes attributes = File.GetAttributes(asarPath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(asarPath, attributes & ~FileAttributes.ReadOnly);

            using (AsarArchive archive = new AsarArchive(asarPath, true))
            {
                int workspaceDependenciesSettingsFunctionEntries =
                    VerifyArchiveIntegrityAndCountJavaScriptPattern(archive,
                        WorkspaceDependenciesSettingsFunctionText);
                if (workspaceDependenciesSettingsFunctionEntries != 1)
                    throw new InvalidDataException(
                        "Electron workspace dependencies settings function is missing or ambiguous.");
                VerifyArchiveJavaScriptPatternState(archive, OfficialConfigModeShortLabelText,
                    PortableConfigModeShortLabelText, ConfigModeShortLabelExpectedOccurrences,
                    "Electron config.toml permission short-label state is invalid.");
                VerifyArchiveJavaScriptPatternState(archive, OfficialConfigModeOptionLabelText,
                    PortableConfigModeOptionLabelText, ConfigModeOptionLabelExpectedOccurrences,
                    "Electron config.toml permission option-label state is invalid.");
                int aumidEntries = 0;
                int portableUserDataResolverEntries = 0;
                int closeToTrayEntries = 0;
                int windowsLastWindowEntries = 0;
                int sparkleGateEntries = 0;
                int workerSparkleGateEntries = 0;
                int updateMenuEntries = 0;
                int runtimeStaticDisabledReasonEntries = 0;
                int runtimeInstallGuardEntries = 0;
                int configModeEquivalenceEntries = 0;
                int configModeShortLabelEntries = 0;
                int configModeOptionLabelEntries = 0;
                int browserPluginAvailabilityEntries = 0;
                int chromePluginAvailabilityEntries = 0;
                int computerUsePluginAvailabilityEntries = 0;
                int browserPluginReconcileAvailabilityEntries = 0;
                int chromePluginReconcileAvailabilityEntries = 0;
                int computerUsePluginReconcileAvailabilityEntries = 0;
                int sitesPluginReconcileAvailabilityEntries = 0;
                int deepResearchPluginReconcileAvailabilityEntries = 0;
                int sunsetUpdateGateEntries = 0;
                int windowsSandboxComposerStateEntries = 0;
                int windowsSandboxBannerMountEntries = 0;
                for (int i = 0; i < archive.Entries.Count; i++)
                {
                    AsarEntry entry = archive.Entries[i];
                    if (entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    {
                        sparkleGateEntries += EnsurePattern(archive, entry,
                            OfficialSparkleGateText, PortableSparkleGateText, 1, true);
                        workerSparkleGateEntries += EnsurePattern(archive, entry,
                            OfficialWorkerSparkleGateText, PortableWorkerSparkleGateText, 1, true);
                        updateMenuEntries += EnsurePattern(archive, entry,
                            OfficialUpdateMenuHandlerText, PortableUpdateMenuHandlerText);
                        runtimeStaticDisabledReasonEntries += EnsurePattern(archive, entry,
                            OfficialRuntimeStaticDisabledReasonText,
                            PortableRuntimeStaticDisabledReasonText);
                        runtimeInstallGuardEntries += EnsurePattern(archive, entry,
                            OfficialRuntimeInstallGuardText, PortableRuntimeInstallGuardText);
                        configModeEquivalenceEntries += EnsurePattern(archive, entry,
                            OfficialConfigModeEquivalenceText,
                            PortableConfigModeEquivalenceText);
                        configModeShortLabelEntries += EnsurePattern(archive, entry,
                            OfficialConfigModeShortLabelText, PortableConfigModeShortLabelText,
                            ConfigModeShortLabelExpectedOccurrences);
                        configModeOptionLabelEntries += EnsurePattern(archive, entry,
                            OfficialConfigModeOptionLabelText, PortableConfigModeOptionLabelText,
                            ConfigModeOptionLabelExpectedOccurrences);
                        browserPluginAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialBrowserPluginAvailabilityText,
                            PortableBrowserPluginAvailabilityText, 1, true);
                        chromePluginAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialChromePluginAvailabilityText,
                            PortableChromePluginAvailabilityText, 1, true);
                        computerUsePluginAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialComputerUsePluginAvailabilityText,
                            PortableComputerUsePluginAvailabilityText, 1, true);
                        browserPluginReconcileAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialBrowserPluginReconcileAvailabilityText,
                            PortableBrowserPluginReconcileAvailabilityText, 1, true);
                        chromePluginReconcileAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialChromePluginReconcileAvailabilityText,
                            PortableChromePluginReconcileAvailabilityText, 1, true);
                        computerUsePluginReconcileAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialComputerUsePluginReconcileAvailabilityText,
                            PortableComputerUsePluginReconcileAvailabilityText, 1, true);
                        sitesPluginReconcileAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialSitesPluginReconcileAvailabilityText,
                            PortableSitesPluginReconcileAvailabilityText, 1, true);
                        deepResearchPluginReconcileAvailabilityEntries += EnsurePattern(archive, entry,
                            OfficialDeepResearchPluginReconcileAvailabilityText,
                            PortableDeepResearchPluginReconcileAvailabilityText, 1, true);
                        sunsetUpdateGateEntries += EnsurePattern(archive, entry,
                            OfficialSunsetUpdateGateText, PortableSunsetUpdateGateText, 1, true);
                        windowsSandboxComposerStateEntries += EnsurePattern(archive, entry,
                            OfficialWindowsSandboxComposerStateText,
                            PortableWindowsSandboxComposerStateText, 1, true);
                        windowsSandboxBannerMountEntries += EnsurePattern(archive, entry,
                            OfficialWindowsSandboxBannerMountText,
                            PortableWindowsSandboxBannerMountText, 1, true);
                        portableUserDataResolverEntries += EnsurePattern(archive, entry,
                            OfficialPortableUserDataResolverText,
                            PortableUserDataResolverText);
                    }
                    if (!entry.Path.StartsWith(BuildJavaScriptPrefix, StringComparison.Ordinal) ||
                        !entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
                    aumidEntries += EnsurePattern(archive, entry, OfficialAumidText, PortableAumidText);
                    closeToTrayEntries += EnsureDirectClosePattern(archive, entry);
                    windowsLastWindowEntries += EnsurePattern(archive, entry,
                        OfficialWindowsLastWindowText, PortableWindowsLastWindowText);
                }
                if (aumidEntries == 0) throw new InvalidDataException("Electron portable AppUserModelID target is missing.");

                if (portableUserDataResolverEntries != 1)
                    throw new InvalidDataException(
                        "Electron portable user-data routing guard is missing or ambiguous.");
                if (closeToTrayEntries != 1)
                    throw new InvalidDataException("Electron close-to-tray target is missing or ambiguous.");
                if (windowsLastWindowEntries != 1)
                    throw new InvalidDataException("Electron Windows last-window target is missing or ambiguous.");


                if (sparkleGateEntries != 1 || workerSparkleGateEntries != 1)
                    throw new InvalidDataException("Electron updater gate is missing or ambiguous.");
                if (updateMenuEntries != 1)
                    throw new InvalidDataException("Electron updater menu target is missing or ambiguous.");

                if (runtimeStaticDisabledReasonEntries != 1 || runtimeInstallGuardEntries != 1)
                    throw new InvalidDataException(
                        "Electron workspace runtime updater target is missing or ambiguous.");
                if (configModeEquivalenceEntries != 1 ||
                    configModeShortLabelEntries != ConfigModeShortLabelExpectedOccurrences ||
                    configModeOptionLabelEntries != ConfigModeOptionLabelExpectedOccurrences)
                    throw new InvalidDataException(
                        "Electron config.toml permission-mode target is missing or ambiguous.");
                if (browserPluginAvailabilityEntries != 1 || chromePluginAvailabilityEntries != 1 ||
                    computerUsePluginAvailabilityEntries != 1 ||
                    browserPluginReconcileAvailabilityEntries != 1 ||
                    chromePluginReconcileAvailabilityEntries != 1 ||
                    computerUsePluginReconcileAvailabilityEntries != 1 ||
                    sitesPluginReconcileAvailabilityEntries != 1 ||
                    deepResearchPluginReconcileAvailabilityEntries != 1)
                    throw new InvalidDataException(
                        "Electron portable plugin-availability target is missing or ambiguous.");
                if (sunsetUpdateGateEntries != 1)
                    throw new InvalidDataException(
                        "Electron forced-update page target is missing or ambiguous.");

                if (windowsSandboxComposerStateEntries != 1)
                    throw new InvalidDataException(
                        "Electron Windows-sandbox composer state target is missing or ambiguous.");

                if (windowsSandboxBannerMountEntries != 1)
                    throw new InvalidDataException(
                        "Electron Windows-sandbox banner mount target is missing or ambiguous.");


                List<OnboardingEntryTarget> onboardingEntries = FindOnboardingEntries(archive);
                for (int i = 0; i < onboardingEntries.Count; i++)
                    EnsureOnboardingEntry(archive, onboardingEntries[i]);
                AsarEntry onboardingHeaderIcon = FindOnboardingHeaderIconEntry(archive);
                EnsureOnboardingHeaderIconEntry(archive, onboardingHeaderIcon);
                archive.FlushHeader();
                archive.Commit();
            }
            if (!IsPrepared(asarPath)) throw new InvalidDataException("Electron portable branding verification failed.");
        }

        internal static bool IsPrepared(string asarPath)
        {
            return HasPreparedState(asarPath);
        }

        private static bool HasPreparedState(string asarPath)
        {
            try
            {
                using (AsarArchive archive = new AsarArchive(asarPath, false))
                {
                    byte[] officialBrand = Encoding.UTF8.GetBytes(OfficialBrandText);
                    byte[] portableBrand = Encoding.UTF8.GetBytes(PortableBrandText);
                    byte[] officialAumid = Encoding.UTF8.GetBytes(OfficialAumidText);
                    byte[] portableAumid = Encoding.UTF8.GetBytes(PortableAumidText);
                    byte[] officialPortableUserDataResolver =
                        Encoding.UTF8.GetBytes(OfficialPortableUserDataResolverText);
                    byte[] portableUserDataResolver =
                        Encoding.UTF8.GetBytes(PortableUserDataResolverText);
                    byte[] officialCloseToTray = Encoding.UTF8.GetBytes(OfficialCloseToTrayText);
                    byte[] legacyPortableCloseToTray = Encoding.UTF8.GetBytes(LegacyPortableCloseToTrayText);
                    byte[] portableCloseToTray = Encoding.UTF8.GetBytes(PortableCloseToTrayText);
                    byte[] portableCloseElectronAlias = Encoding.UTF8.GetBytes(PortableCloseElectronAliasText);
                    byte[] officialWindowsLastWindow = Encoding.UTF8.GetBytes(OfficialWindowsLastWindowText);
                    byte[] portableWindowsLastWindow = Encoding.UTF8.GetBytes(PortableWindowsLastWindowText);
                    byte[] officialWindowsWindowIconSelector =
                        Encoding.UTF8.GetBytes(OfficialWindowsWindowIconSelectorText);
                    byte[] portableWindowsWindowIconSelector =
                        Encoding.UTF8.GetBytes(PortableWindowsWindowIconSelectorText);
                    byte[] officialWindowsWindowIconResolver =
                        Encoding.UTF8.GetBytes(OfficialWindowsWindowIconResolverText);
                    byte[] portableWindowsWindowIconResolver =
                        Encoding.UTF8.GetBytes(PortableWindowsWindowIconResolverText);
                    int portableAumidOccurrences = 0;
                    int portableUserDataResolverOccurrences = 0;
                    int portableCloseToTrayOccurrences = 0;
                    int portableWindowsLastWindowOccurrences = 0;
                    int portableWindowsWindowIconSelectorOccurrences = 0;
                    int portableWindowsWindowIconResolverOccurrences = 0;
                    for (int i = 0; i < archive.Entries.Count; i++)
                    {
                        AsarEntry entry = archive.Entries[i];
                        if (!entry.Path.StartsWith(BuildJavaScriptPrefix, StringComparison.Ordinal) ||
                            !entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
                        byte[] bytes = archive.ReadEntry(entry);
                        int officialAumidCount = CountPattern(bytes, officialAumid);
                        int portableAumidCount = CountPattern(bytes, portableAumid);
                        int officialPortableUserDataResolverCount =
                            CountPattern(bytes, officialPortableUserDataResolver);
                        int portableUserDataResolverCount =
                            CountPattern(bytes, portableUserDataResolver);
                        int officialCloseToTrayCount = CountPattern(bytes, officialCloseToTray);
                        int legacyPortableCloseToTrayCount = CountPattern(bytes, legacyPortableCloseToTray);
                        int portableCloseToTrayCount = CountPattern(bytes, portableCloseToTray);
                        int officialWindowsLastWindowCount = CountPattern(bytes, officialWindowsLastWindow);
                        int portableWindowsLastWindowCount = CountPattern(bytes, portableWindowsLastWindow);
                        int officialWindowsWindowIconSelectorCount =
                            CountPattern(bytes, officialWindowsWindowIconSelector);
                        int portableWindowsWindowIconSelectorCount =
                            CountPattern(bytes, portableWindowsWindowIconSelector);
                        int officialWindowsWindowIconResolverCount =
                            CountPattern(bytes, officialWindowsWindowIconResolver);
                        int portableWindowsWindowIconResolverCount =
                            CountPattern(bytes, portableWindowsWindowIconResolver);
                        if (officialAumidCount != 0 ||
                            legacyPortableCloseToTrayCount != 0 ||
                            officialWindowsWindowIconResolverCount != 0) return false;
                        if (portableAumidCount > 1 || portableUserDataResolverCount > 1 ||
                            portableCloseToTrayCount > 1 ||
                            portableWindowsLastWindowCount > 1 ||
                            portableWindowsWindowIconSelectorCount > 1 ||
                            portableWindowsWindowIconResolverCount > 1) return false;
                        if (portableAumidCount == 0 && portableUserDataResolverCount == 0 &&
                            portableCloseToTrayCount == 0 &&
                            portableWindowsLastWindowCount == 0 &&
                            portableWindowsWindowIconSelectorCount == 0 &&
                            portableWindowsWindowIconResolverCount == 0) continue;
                        if (portableCloseToTrayCount != 0 &&
                            CountPattern(bytes, portableCloseElectronAlias) != 1) return false;
                        portableAumidOccurrences += portableAumidCount;
                        portableUserDataResolverOccurrences += portableUserDataResolverCount;
                        portableCloseToTrayOccurrences += portableCloseToTrayCount;
                        portableWindowsLastWindowOccurrences += portableWindowsLastWindowCount;
                        portableWindowsWindowIconSelectorOccurrences +=
                            portableWindowsWindowIconSelectorCount;
                        portableWindowsWindowIconResolverOccurrences +=
                            portableWindowsWindowIconResolverCount;
                        if (!IntegrityMatches(entry, ComputeIntegrity(bytes, entry.BlockSize))) return false;
                    }
                    if (portableAumidOccurrences == 0 ||
                        portableUserDataResolverOccurrences != 1 ||
                        portableCloseToTrayOccurrences != 1 ||
                        portableWindowsLastWindowOccurrences != 1) return false;

                    byte[] officialSparkleGate = Encoding.UTF8.GetBytes(OfficialSparkleGateText);
                    byte[] portableSparkleGate = Encoding.UTF8.GetBytes(PortableSparkleGateText);
                    byte[] officialWorkerSparkleGate = Encoding.UTF8.GetBytes(OfficialWorkerSparkleGateText);
                    byte[] portableWorkerSparkleGate = Encoding.UTF8.GetBytes(PortableWorkerSparkleGateText);
                    byte[] officialUpdateMenu = Encoding.UTF8.GetBytes(OfficialUpdateMenuHandlerText);
                    byte[] portableUpdateMenu = Encoding.UTF8.GetBytes(PortableUpdateMenuHandlerText);
                    byte[] officialUpdaterIdleState = Encoding.UTF8.GetBytes(OfficialUpdaterIdleStateText);
                    byte[] portableUpdaterIdleState = Encoding.UTF8.GetBytes(PortableUpdaterIdleStateText);
                    byte[] officialRuntimeStaticDisabledReason =
                        Encoding.UTF8.GetBytes(OfficialRuntimeStaticDisabledReasonText);
                    byte[] portableRuntimeStaticDisabledReason =
                        Encoding.UTF8.GetBytes(PortableRuntimeStaticDisabledReasonText);
                    byte[] officialRuntimeInstallGuard =
                        Encoding.UTF8.GetBytes(OfficialRuntimeInstallGuardText);
                    byte[] portableRuntimeInstallGuard =
                        Encoding.UTF8.GetBytes(PortableRuntimeInstallGuardText);
                    byte[] officialWorkspaceDependenciesSettingsPanelGate =
                        Encoding.UTF8.GetBytes(OfficialWorkspaceDependenciesSettingsPanelGateText);
                    byte[] portableWorkspaceDependenciesSettingsPanelGate =
                        Encoding.UTF8.GetBytes(PortableWorkspaceDependenciesSettingsPanelGateText);
                    byte[] officialConfigModeEquivalence =
                        Encoding.UTF8.GetBytes(OfficialConfigModeEquivalenceText);
                    byte[] portableConfigModeEquivalence =
                        Encoding.UTF8.GetBytes(PortableConfigModeEquivalenceText);
                    byte[] officialConfigModeShortLabel =
                        Encoding.UTF8.GetBytes(OfficialConfigModeShortLabelText);
                    byte[] portableConfigModeShortLabel =
                        Encoding.UTF8.GetBytes(PortableConfigModeShortLabelText);
                    byte[] officialConfigModeOptionLabel =
                        Encoding.UTF8.GetBytes(OfficialConfigModeOptionLabelText);
                    byte[] portableConfigModeOptionLabel =
                        Encoding.UTF8.GetBytes(PortableConfigModeOptionLabelText);
                    byte[] officialBrowserPluginAvailability =
                        Encoding.UTF8.GetBytes(OfficialBrowserPluginAvailabilityText);
                    byte[] portableBrowserPluginAvailability =
                        Encoding.UTF8.GetBytes(PortableBrowserPluginAvailabilityText);
                    byte[] officialChromePluginAvailability =
                        Encoding.UTF8.GetBytes(OfficialChromePluginAvailabilityText);
                    byte[] portableChromePluginAvailability =
                        Encoding.UTF8.GetBytes(PortableChromePluginAvailabilityText);
                    byte[] officialComputerUsePluginAvailability =
                        Encoding.UTF8.GetBytes(OfficialComputerUsePluginAvailabilityText);
                    byte[] portableComputerUsePluginAvailability =
                        Encoding.UTF8.GetBytes(PortableComputerUsePluginAvailabilityText);
                    byte[] officialSunsetUpdateGate =
                        Encoding.UTF8.GetBytes(OfficialSunsetUpdateGateText);
                    byte[] portableSunsetUpdateGate =
                        Encoding.UTF8.GetBytes(PortableSunsetUpdateGateText);
                    byte[] officialTryModelAvailabilityGate =
                        Encoding.UTF8.GetBytes(OfficialTryModelAvailabilityGateText);
                    byte[] portableTryModelAvailabilityGate =
                        Encoding.UTF8.GetBytes(PortableTryModelAvailabilityGateText);
                    byte[] officialTryModelUpgradeGate =
                        Encoding.UTF8.GetBytes(OfficialTryModelUpgradeGateText);
                    byte[] portableTryModelUpgradeGate =
                        Encoding.UTF8.GetBytes(PortableTryModelUpgradeGateText);
                    byte[] officialWindowsSandboxComposerState =
                        Encoding.UTF8.GetBytes(OfficialWindowsSandboxComposerStateText);
                    byte[] portableWindowsSandboxComposerState =
                        Encoding.UTF8.GetBytes(PortableWindowsSandboxComposerStateText);
                    int portableSparkleGateOccurrences = 0;
                    int portableWorkerSparkleGateOccurrences = 0;
                    int portableUpdateMenuOccurrences = 0;
                    int portableBrandOccurrences = 0;
                    int portableUpdaterIdleStateOccurrences = 0;
                    int portableRuntimeStaticDisabledReasonOccurrences = 0;
                    int portableRuntimeInstallGuardOccurrences = 0;
                    int workspaceDependenciesSettingsFunctionOccurrences = 0;
                    int officialWorkspaceDependenciesSettingsPanelGateOccurrences = 0;
                    int portableWorkspaceDependenciesSettingsPanelGateOccurrences = 0;
                    int portableConfigModeEquivalenceOccurrences = 0;
                    int portableConfigModeShortLabelOccurrences = 0;
                    int portableConfigModeOptionLabelOccurrences = 0;
                    int portableBrowserPluginAvailabilityOccurrences = 0;
                    int portableChromePluginAvailabilityOccurrences = 0;
                    int portableComputerUsePluginAvailabilityOccurrences = 0;
                    int portableBrowserPluginReconcileAvailabilityOccurrences = 0;
                    int portableChromePluginReconcileAvailabilityOccurrences = 0;
                    int portableComputerUsePluginReconcileAvailabilityOccurrences = 0;
                    int portableSitesPluginReconcileAvailabilityOccurrences = 0;
                    int portableDeepResearchPluginReconcileAvailabilityOccurrences = 0;
                    int portableSunsetUpdateGateOccurrences = 0;
                    int portableTryModelAvailabilityGateOccurrences = 0;
                    int portableTryModelUpgradeGateOccurrences = 0;
                    int portableWindowsSandboxComposerStateOccurrences = 0;
                    int portableWindowsSandboxReadinessStateOccurrences = 0;
                    for (int i = 0; i < archive.Entries.Count; i++)
                    {
                        AsarEntry entry = archive.Entries[i];
                        if (!entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
                        byte[] bytes = archive.ReadEntry(entry);
                        int officialBrandCount = CountIdentifierPattern(bytes, OfficialBrandText, entry);
                        int portableBrandCount = CountIdentifierPattern(bytes, PortableBrandText, entry);
                        int officialSparkleGateCount = CountIdentifierPattern(bytes,
                            OfficialSparkleGateText, entry);
                        int portableSparkleGateCount = CountIdentifierPattern(bytes,
                            PortableSparkleGateText, entry);
                        int officialWorkerSparkleGateCount = CountIdentifierPattern(bytes,
                            OfficialWorkerSparkleGateText, entry);
                        int portableWorkerSparkleGateCount = CountIdentifierPattern(bytes,
                            PortableWorkerSparkleGateText, entry);
                        int officialUpdateMenuCount = CountPattern(bytes, officialUpdateMenu);
                        int portableUpdateMenuCount = CountPattern(bytes, portableUpdateMenu);
                        int officialUpdaterIdleStateCount = CountPattern(bytes, officialUpdaterIdleState);
                        int portableUpdaterIdleStateCount = CountPattern(bytes, portableUpdaterIdleState);
                        int officialRuntimeStaticDisabledReasonCount =
                            CountPattern(bytes, officialRuntimeStaticDisabledReason);
                        int portableRuntimeStaticDisabledReasonCount =
                            CountPattern(bytes, portableRuntimeStaticDisabledReason);
                        int officialRuntimeInstallGuardCount =
                            CountPattern(bytes, officialRuntimeInstallGuard);
                        int portableRuntimeInstallGuardCount =
                            CountPattern(bytes, portableRuntimeInstallGuard);
                        int workspaceDependenciesSettingsFunctionCount =
                            CountIdentifierPattern(bytes,
                                WorkspaceDependenciesSettingsFunctionText, entry);
                        int officialWorkspaceDependenciesSettingsPanelGateCount =
                            CountPattern(bytes, officialWorkspaceDependenciesSettingsPanelGate);
                        int portableWorkspaceDependenciesSettingsPanelGateCount =
                            CountPattern(bytes, portableWorkspaceDependenciesSettingsPanelGate);
                        int officialConfigModeEquivalenceCount =
                            CountPattern(bytes, officialConfigModeEquivalence);
                        int portableConfigModeEquivalenceCount =
                            CountPattern(bytes, portableConfigModeEquivalence);
                        int officialConfigModeShortLabelCount =
                            CountPattern(bytes, officialConfigModeShortLabel);
                        int portableConfigModeShortLabelCount =
                            CountPattern(bytes, portableConfigModeShortLabel);
                        int officialConfigModeOptionLabelCount =
                            CountPattern(bytes, officialConfigModeOptionLabel);
                        int portableConfigModeOptionLabelCount =
                            CountPattern(bytes, portableConfigModeOptionLabel);
                        int officialBrowserPluginAvailabilityCount =
                            CountIdentifierPattern(bytes, OfficialBrowserPluginAvailabilityText, entry);
                        int portableBrowserPluginAvailabilityCount =
                            CountIdentifierPattern(bytes, PortableBrowserPluginAvailabilityText, entry);
                        int officialChromePluginAvailabilityCount =
                            CountIdentifierPattern(bytes, OfficialChromePluginAvailabilityText, entry);
                        int portableChromePluginAvailabilityCount =
                            CountIdentifierPattern(bytes, PortableChromePluginAvailabilityText, entry);
                        int officialComputerUsePluginAvailabilityCount =
                            CountIdentifierPattern(bytes, OfficialComputerUsePluginAvailabilityText, entry);
                        int portableComputerUsePluginAvailabilityCount =
                            CountIdentifierPattern(bytes, PortableComputerUsePluginAvailabilityText, entry);
                        int officialBrowserPluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                OfficialBrowserPluginReconcileAvailabilityText, entry);
                        int portableBrowserPluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                PortableBrowserPluginReconcileAvailabilityText, entry);
                        int officialChromePluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                OfficialChromePluginReconcileAvailabilityText, entry);
                        int portableChromePluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                PortableChromePluginReconcileAvailabilityText, entry);
                        int officialComputerUsePluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                OfficialComputerUsePluginReconcileAvailabilityText, entry);
                        int portableComputerUsePluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                PortableComputerUsePluginReconcileAvailabilityText, entry);
                        int officialSitesPluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                OfficialSitesPluginReconcileAvailabilityText, entry);
                        int portableSitesPluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                PortableSitesPluginReconcileAvailabilityText, entry);
                        int officialDeepResearchPluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                OfficialDeepResearchPluginReconcileAvailabilityText, entry);
                        int portableDeepResearchPluginReconcileAvailabilityCount =
                            CountIdentifierPattern(bytes,
                                PortableDeepResearchPluginReconcileAvailabilityText, entry);
                        int officialSunsetUpdateGateCount = CountIdentifierPattern(bytes,
                            OfficialSunsetUpdateGateText, entry);
                        int portableSunsetUpdateGateCount = CountIdentifierPattern(bytes,
                            PortableSunsetUpdateGateText, entry);
                        int officialTryModelAvailabilityGateCount =
                            CountIdentifierPattern(bytes, OfficialTryModelAvailabilityGateText, entry);
                        int portableTryModelAvailabilityGateCount =
                            CountIdentifierPattern(bytes, PortableTryModelAvailabilityGateText, entry);
                        int officialTryModelUpgradeGateCount =
                            CountIdentifierPattern(bytes, OfficialTryModelUpgradeGateText, entry);
                        int portableTryModelUpgradeGateCount =
                            CountIdentifierPattern(bytes, PortableTryModelUpgradeGateText, entry);
                        int officialWindowsSandboxComposerStateCount =
                            CountIdentifierPattern(bytes, OfficialWindowsSandboxComposerStateText, entry);
                        int portableWindowsSandboxComposerStateCount =
                            CountIdentifierPattern(bytes, PortableWindowsSandboxComposerStateText, entry);
                        int officialWindowsSandboxReadinessStateCount =
                            CountIdentifierPattern(bytes, OfficialWindowsSandboxReadinessStateText, entry);
                        int portableWindowsSandboxReadinessStateCount =
                            CountIdentifierPattern(bytes, PortableWindowsSandboxReadinessStateText, entry);
                        
                        if (portableBrandCount == 0 && portableSparkleGateCount == 0 && portableWorkerSparkleGateCount == 0 &&
                            portableUpdateMenuCount == 0 &&
                            portableUpdaterIdleStateCount == 0 &&
                            portableRuntimeStaticDisabledReasonCount == 0 &&
                            portableRuntimeInstallGuardCount == 0 &&
                            workspaceDependenciesSettingsFunctionCount == 0 &&
                            officialWorkspaceDependenciesSettingsPanelGateCount == 0 &&
                            portableWorkspaceDependenciesSettingsPanelGateCount == 0 &&
                            portableConfigModeEquivalenceCount == 0 &&
                            portableConfigModeShortLabelCount == 0 &&
                            portableConfigModeOptionLabelCount == 0 &&
                            portableBrowserPluginAvailabilityCount == 0 &&
                            portableChromePluginAvailabilityCount == 0 &&
                            portableComputerUsePluginAvailabilityCount == 0 &&
                            portableBrowserPluginReconcileAvailabilityCount == 0 &&
                            portableChromePluginReconcileAvailabilityCount == 0 &&
                            portableComputerUsePluginReconcileAvailabilityCount == 0 &&
                            portableSitesPluginReconcileAvailabilityCount == 0 &&
                            portableDeepResearchPluginReconcileAvailabilityCount == 0 &&
                            portableSunsetUpdateGateCount == 0 &&
                            portableTryModelAvailabilityGateCount == 0 &&
                            portableTryModelUpgradeGateCount == 0 &&
                            portableWindowsSandboxComposerStateCount == 0 &&
                            portableWindowsSandboxReadinessStateCount == 0) continue;
                        if (!IntegrityMatches(entry, ComputeIntegrity(bytes, entry.BlockSize))) return false;
                        portableBrandOccurrences += portableBrandCount;
                        portableSparkleGateOccurrences += portableSparkleGateCount;
                        portableWorkerSparkleGateOccurrences += portableWorkerSparkleGateCount;
                        portableUpdateMenuOccurrences += portableUpdateMenuCount;
                        portableUpdaterIdleStateOccurrences += portableUpdaterIdleStateCount;
                        portableRuntimeStaticDisabledReasonOccurrences +=
                            portableRuntimeStaticDisabledReasonCount;
                        portableRuntimeInstallGuardOccurrences += portableRuntimeInstallGuardCount;
                        workspaceDependenciesSettingsFunctionOccurrences +=
                            workspaceDependenciesSettingsFunctionCount;
                        officialWorkspaceDependenciesSettingsPanelGateOccurrences +=
                            officialWorkspaceDependenciesSettingsPanelGateCount;
                        portableWorkspaceDependenciesSettingsPanelGateOccurrences +=
                            portableWorkspaceDependenciesSettingsPanelGateCount;
                        portableConfigModeEquivalenceOccurrences +=
                            portableConfigModeEquivalenceCount;
                        portableConfigModeShortLabelOccurrences +=
                            portableConfigModeShortLabelCount;
                        portableConfigModeOptionLabelOccurrences +=
                            portableConfigModeOptionLabelCount;
                        portableBrowserPluginAvailabilityOccurrences +=
                            portableBrowserPluginAvailabilityCount;
                        portableChromePluginAvailabilityOccurrences +=
                            portableChromePluginAvailabilityCount;
                        portableComputerUsePluginAvailabilityOccurrences +=
                            portableComputerUsePluginAvailabilityCount;
                        portableBrowserPluginReconcileAvailabilityOccurrences +=
                            portableBrowserPluginReconcileAvailabilityCount;
                        portableChromePluginReconcileAvailabilityOccurrences +=
                            portableChromePluginReconcileAvailabilityCount;
                        portableComputerUsePluginReconcileAvailabilityOccurrences +=
                            portableComputerUsePluginReconcileAvailabilityCount;
                        portableSitesPluginReconcileAvailabilityOccurrences +=
                            portableSitesPluginReconcileAvailabilityCount;
                        portableDeepResearchPluginReconcileAvailabilityOccurrences +=
                            portableDeepResearchPluginReconcileAvailabilityCount;
                        portableSunsetUpdateGateOccurrences += portableSunsetUpdateGateCount;
                        portableTryModelAvailabilityGateOccurrences +=
                            portableTryModelAvailabilityGateCount;
                        portableTryModelUpgradeGateOccurrences +=
                            portableTryModelUpgradeGateCount;
                        portableWindowsSandboxComposerStateOccurrences +=
                            portableWindowsSandboxComposerStateCount;
                        portableWindowsSandboxReadinessStateOccurrences +=
                            portableWindowsSandboxReadinessStateCount;
                    }
                    if (portableSparkleGateOccurrences != 1 ||
                        portableWorkerSparkleGateOccurrences != 1 ||
                        portableUpdateMenuOccurrences != 1 ||
                        portableRuntimeStaticDisabledReasonOccurrences != 1 ||
                        portableRuntimeInstallGuardOccurrences != 1 ||
                        portableConfigModeEquivalenceOccurrences != 1 ||
                        portableConfigModeShortLabelOccurrences != ConfigModeShortLabelExpectedOccurrences ||
                        portableConfigModeOptionLabelOccurrences != ConfigModeOptionLabelExpectedOccurrences ||
                        portableBrowserPluginAvailabilityOccurrences != 1 ||
                        portableChromePluginAvailabilityOccurrences != 1 ||
                        portableComputerUsePluginAvailabilityOccurrences != 1 ||
                        portableBrowserPluginReconcileAvailabilityOccurrences != 1 ||
                        portableChromePluginReconcileAvailabilityOccurrences != 1 ||
                        portableComputerUsePluginReconcileAvailabilityOccurrences != 1 ||
                        portableSitesPluginReconcileAvailabilityOccurrences != 1 ||
                        portableDeepResearchPluginReconcileAvailabilityOccurrences != 1 ||
                        portableSunsetUpdateGateOccurrences != 1 ||
                        portableWindowsSandboxComposerStateOccurrences != 1) return false;

                    List<OnboardingEntryTarget> onboardingEntries = FindOnboardingEntries(archive);
                    int officialStandardOnboardingOccurrences = 0;
                    int portableStandardOnboardingOccurrences = 0;
                    for (int i = 0; i < onboardingEntries.Count; i++)
                    {
                        OnboardingEntryTarget target = onboardingEntries[i];
                        byte[] bytes = archive.ReadEntry(target.Entry);
                        List<OnboardingLiteralTarget> literals = AnalyzeOnboardingEntry(bytes, target);
                        officialStandardOnboardingOccurrences += CountIdentifierPattern(bytes,
                            OfficialStandardOnboardingGateText, target.Entry);
                        portableStandardOnboardingOccurrences += CountIdentifierPattern(bytes,
                            PortableStandardOnboardingGateText, target.Entry);
                        if (!literals[0].IsPortable || !literals[1].IsPortable ||
                            !IntegrityMatches(target.Entry,
                                ComputeIntegrity(bytes, target.Entry.BlockSize))) return false;
                    }
                    if (officialStandardOnboardingOccurrences != 0 ||
                        portableStandardOnboardingOccurrences != 1) return false;

                    AsarEntry onboardingHeaderIcon = FindOnboardingHeaderIconEntry(archive);
                    byte[] onboardingHeaderIconBytes = archive.ReadEntry(onboardingHeaderIcon);
                    if (!HasOnboardingHeaderIconState(onboardingHeaderIconBytes, true,
                            onboardingHeaderIcon) ||
                        !IntegrityMatches(onboardingHeaderIcon,
                            ComputeIntegrity(onboardingHeaderIconBytes,
                                onboardingHeaderIcon.BlockSize))) return false;
                    return true;
                }
            }
            catch { return false; }
        }

        private static List<OnboardingEntryTarget> FindOnboardingEntries(AsarArchive archive)
        {
            List<OnboardingEntryTarget> result = new List<OnboardingEntryTarget>();
            AsarEntry onboardingPage = null;
            for (int i = 0; i < archive.Entries.Count; i++)
            {
                AsarEntry candidate = archive.Entries[i];
                if (!IsWebviewJavaScriptAsset(candidate.Path)) continue;
                byte[] bytes = archive.ReadEntry(candidate);
                if (HasOnboardingDefaultMessageKeys(bytes))
                {
                    if (onboardingPage != null)
                        throw new InvalidDataException("Electron onboarding default-message asset is ambiguous.");
                    onboardingPage = candidate;
                    continue;
                }
                if (!HasOnboardingLocaleMessageKeys(bytes)) continue;
                OnboardingEntryTarget localized = new OnboardingEntryTarget();
                localized.Entry = candidate;
                localized.ContainsDefaultMessages = false;
                result.Add(localized);
            }
            if (onboardingPage == null)
                throw new InvalidDataException("Electron onboarding default-message asset is missing.");
            OnboardingEntryTarget defaultMessages = new OnboardingEntryTarget();
            defaultMessages.Entry = onboardingPage;
            defaultMessages.ContainsDefaultMessages = true;
            result.Add(defaultMessages);
            return result;
        }

        private static bool HasOnboardingLocaleMessageKeys(byte[] bytes)
        {
            byte[] rolePrefix = Encoding.UTF8.GetBytes("\"" +
                OnboardingMessageIdPrefix + "roleOnlyWelcomeIntroduction\":");
            byte[] welcomePrefix = Encoding.UTF8.GetBytes("\"" +
                OnboardingMessageIdPrefix + "welcomeIntroduction\":");
            return CountPattern(bytes, rolePrefix) == 1 && CountPattern(bytes, welcomePrefix) == 1;
        }

        private static bool HasOnboardingDefaultMessageKeys(byte[] bytes)
        {
            byte[] rolePrefix = Encoding.UTF8.GetBytes("roleOnlyWelcomeIntroduction:{id:`" +
                OnboardingMessageIdPrefix + "roleOnlyWelcomeIntroduction`,defaultMessage:");
            byte[] welcomePrefix = Encoding.UTF8.GetBytes("welcomeIntroduction:{id:`" +
                OnboardingMessageIdPrefix + "welcomeIntroduction`,defaultMessage:");
            return CountPattern(bytes, rolePrefix) == 1 && CountPattern(bytes, welcomePrefix) == 1;
        }

        private static bool IsWebviewJavaScriptAsset(string path)
        {
            return path.StartsWith(WebviewAssetPrefix, StringComparison.Ordinal) &&
                path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHashedWebviewAsset(string path, string stem)
        {
            string prefix = WebviewAssetPrefix + stem + "-";
            const string extension = ".js";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
            int hashLength = path.Length - prefix.Length - extension.Length;
            if (hashLength < 1) return false;
            for (int i = prefix.Length; i < prefix.Length + hashLength; i++)
            {
                char value = path[i];
                if ((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                    (value >= '0' && value <= '9') || value == '-' || value == '_') continue;
                return false;
            }
            return true;
        }

        private static AsarEntry FindOnboardingHeaderIconEntry(AsarArchive archive)
        {
            AsarEntry result = null;
            for (int i = 0; i < archive.Entries.Count; i++)
            {
                AsarEntry candidate = archive.Entries[i];
                if (!IsHashedWebviewAsset(candidate.Path, AppInitialAssetStem)) continue;
                if (result != null)
                    throw new InvalidDataException("Electron onboarding header icon asset is ambiguous.");
                result = candidate;
            }
            if (result == null)
                throw new InvalidDataException("Electron onboarding header icon asset is missing.");
            return result;
        }

        private static void EnsureOnboardingHeaderIconEntry(AsarArchive archive, AsarEntry entry)
        {
            // The minifier renames the JSX aliases between desktop releases.
            // Locate the one stable header container/icon pair and rewrite only
            // its condition to equal-length JavaScript whitespace. This keeps
            // ASAR offsets stable without binding the patch to volatile aliases.
            archive.FlushHeader();
            byte[] currentBytes = archive.ReadEntry(entry);
            int conditionOffset;
            int conditionLength;
            bool isPortable;
            if (!TryFindOnboardingHeaderIconCondition(currentBytes, out conditionOffset,
                    out conditionLength, out isPortable))
                throw new InvalidDataException(
                    "Electron onboarding header icon target is missing or ambiguous: " +
                    entry.Path);
            if (isPortable)
            {
                // If a previous run wrote the entry before its header hash,
                // the portable bytes are still a known, intentional state.  A
                // fresh hash repairs that interrupted state while preserving
                // the offset-bound header update.  Unknown bytes never reach
                // this branch because TryFindOnboardingHeaderIconCondition
                // only accepts the exact portable replacement shape.
                IntegrityState repairedIntegrity = ComputeIntegrity(currentBytes,
                    entry.BlockSize);
                if (!IntegrityMatches(entry, repairedIntegrity))
                    archive.AddIntegrityReplacement(entry, repairedIntegrity);
                return;
            }

            byte[] portableBytes = (byte[])currentBytes.Clone();
            portableBytes[conditionOffset] = (byte)'0';
            for (int i = 1; i < conditionLength; i++)
                portableBytes[conditionOffset + i] = (byte)' ';
            IntegrityState currentIntegrity = ComputeIntegrity(currentBytes, entry.BlockSize);
            IntegrityState portableIntegrity = ComputeIntegrity(portableBytes, entry.BlockSize);
            bool headerIsCurrent = IntegrityMatches(entry, currentIntegrity);
            bool headerIsPortable = IntegrityMatches(entry, portableIntegrity);
            if (!headerIsCurrent && !headerIsPortable)
                throw new InvalidDataException(
                    "Electron onboarding header icon entry failed integrity verification: " +
                    entry.Path);
            // The entry bytes and ASAR header can be observed between the two
            // writes after an interrupted prior run. Always converge official
            // bytes to the portable state; only the header update is conditional.
            archive.WriteEntry(entry, portableBytes);
            if (headerIsCurrent)
                archive.AddIntegrityReplacement(entry, portableIntegrity);
        }

        private static bool HasOnboardingHeaderIconState(byte[] bytes, bool portable,
            AsarEntry entry)
        {
            int conditionOffset;
            int conditionLength;
            bool isPortable;
            if (!TryFindOnboardingHeaderIconCondition(bytes, out conditionOffset,
                    out conditionLength, out isPortable)) return false;
            return isPortable == portable;
        }

        private static bool TryFindOnboardingHeaderIconCondition(byte[] bytes,
            out int conditionOffset, out int conditionLength, out bool isPortable)
        {
            conditionOffset = -1;
            conditionLength = 0;
            isPortable = false;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return false; }
            if (CountText(text, OnboardingHeaderContainerClassText) != 1 ||
                CountText(text, OnboardingHeaderIconClassText) != 1) return false;
            int container = text.IndexOf(OnboardingHeaderContainerClassText,
                StringComparison.Ordinal);
            int iconClass = text.IndexOf(OnboardingHeaderIconClassText,
                container + OnboardingHeaderContainerClassText.Length,
                StringComparison.Ordinal);
            if (container < 0 || iconClass < 0) return false;
            int conditional = text.LastIndexOf('?', container);
            if (conditional < 0 || conditional + 1 >= text.Length ||
                text[conditional + 1] != '(') return false;
            int tokenEnd = conditional;
            while (tokenEnd > 0 && text[tokenEnd - 1] == ' ') tokenEnd--;
            if (tokenEnd > 0 && text[tokenEnd - 1] == '0' &&
                tokenEnd > 1 && text[tokenEnd - 2] == '=')
            {
                int portableStart = tokenEnd - 1;
                conditionOffset = Encoding.UTF8.GetByteCount(text.Substring(0, portableStart));
                conditionLength = Encoding.UTF8.GetByteCount(text.Substring(portableStart,
                    conditional - portableStart));
                isPortable = true;
                return true;
            }
            int start = tokenEnd - 1;
            while (start >= 0 && IsJavaScriptIdentifierPart(text[start])) start--;
            start++;
            if (start >= tokenEnd || !IsJavaScriptIdentifierStart(text[start])) return false;
            for (int i = start; i < tokenEnd; i++)
                if (!IsJavaScriptIdentifierPart(text[i])) return false;
            if (start == 0 || text[start - 1] != '=') return false;
            conditionOffset = Encoding.UTF8.GetByteCount(text.Substring(0, start));
            conditionLength = Encoding.UTF8.GetByteCount(text.Substring(start,
                conditional - start));
            // An upstream minifier condition is an identifier. Replacing it
            // with `0` plus whitespace preserves both syntax and byte offsets.
            return conditionLength > 0;
        }

        private static int CountText(string text, string value)
        {
            int count = 0;
            int offset = 0;
            while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }
            return count;
        }

        private static void EnsureOnboardingEntry(AsarArchive archive, OnboardingEntryTarget target)
        {
            if (target.ContainsDefaultMessages &&
                EnsurePattern(archive, target.Entry, OfficialWindowsSandboxFinalStepText,
                    PortableWindowsSandboxFinalStepText, 1, true) != 1)
                throw new InvalidDataException(
                    "Electron Windows-sandbox onboarding final-step target is missing or ambiguous: " +
                    target.Entry.Path);
            archive.FlushHeader();
            byte[] currentBytes = archive.ReadEntry(target.Entry);
            List<OnboardingLiteralTarget> currentLiterals = AnalyzeOnboardingEntry(currentBytes, target);
            bool isPortable = currentLiterals[0].IsPortable;
            if (currentLiterals[1].IsPortable != isPortable)
                throw new InvalidDataException("Electron onboarding entry contains mixed branding: " +
                    target.Entry.Path);

            byte[] officialGate = Encoding.UTF8.GetBytes(OfficialStandardOnboardingGateText);
            byte[] portableGate = Encoding.UTF8.GetBytes(PortableStandardOnboardingGateText);
            if (officialGate.Length != portableGate.Length)
                throw new InvalidDataException("Electron onboarding gate replacement must preserve entry length.");
            int officialGateCount = CountIdentifierPattern(currentBytes,
                OfficialStandardOnboardingGateText, target.Entry);
            int portableGateCount = CountIdentifierPattern(currentBytes,
                PortableStandardOnboardingGateText, target.Entry);
            bool gateIsPortable = portableGateCount == 1;
            if (target.ContainsDefaultMessages)
            {
                if (officialGateCount + portableGateCount != 1)
                    throw new InvalidDataException(
                        "Electron standard-onboarding gate is missing, mixed, or ambiguous: " +
                        target.Entry.Path);
            }
            else if (officialGateCount != 0 || portableGateCount != 0)
                throw new InvalidDataException(
                    "Electron standard-onboarding gate appeared in a locale asset: " +
                    target.Entry.Path);

            byte[] originalBytes = (byte[])currentBytes.Clone();
            if (isPortable)
            {
                for (int i = 0; i < currentLiterals.Count; i++)
                    ConvertOnboardingLiteral(originalBytes, currentLiterals[i], false);
            }
            if (target.ContainsDefaultMessages && gateIsPortable)
                ReplacePattern(originalBytes, portableGate, officialGate);

            byte[] legacyPortableBytes = (byte[])originalBytes.Clone();
            List<OnboardingLiteralTarget> originalLiterals = AnalyzeOnboardingEntry(originalBytes, target);
            for (int i = 0; i < originalLiterals.Count; i++)
                ConvertOnboardingLiteral(legacyPortableBytes, originalLiterals[i], true);

            byte[] portableBytes = (byte[])legacyPortableBytes.Clone();
            if (target.ContainsDefaultMessages)
                portableBytes = RewriteIdentifierPattern(portableBytes,
                    OfficialStandardOnboardingGateText, PortableStandardOnboardingGateText);

            ValidateOnboardingState(originalBytes, target, false);
            ValidateOnboardingState(legacyPortableBytes, target, true);
            ValidateOnboardingState(portableBytes, target, true);
            if (target.ContainsDefaultMessages)
            {
                if (!HasStandardOnboardingGateState(originalBytes, false) ||
                    !HasStandardOnboardingGateState(legacyPortableBytes, false) ||
                    !HasStandardOnboardingGateState(portableBytes, true))
                    throw new InvalidDataException(
                        "Electron standard-onboarding gate transformation failed: " +
                        target.Entry.Path);
            }
            IntegrityState originalIntegrity = ComputeIntegrity(originalBytes, target.Entry.BlockSize);
            IntegrityState legacyPortableIntegrity = ComputeIntegrity(legacyPortableBytes,
                target.Entry.BlockSize);
            IntegrityState portableIntegrity = ComputeIntegrity(portableBytes, target.Entry.BlockSize);
            // The legacy state is branding-only (through 1.2.4). Accept it so existing
            // portable payloads upgrade in place, while still rejecting unrecognized bytes.
            bool headerIsOriginal = IntegrityMatches(target.Entry, originalIntegrity);
            bool headerIsLegacyPortable = IntegrityMatches(target.Entry, legacyPortableIntegrity);
            bool headerIsPortable = IntegrityMatches(target.Entry, portableIntegrity);
            if (!headerIsOriginal && !headerIsLegacyPortable && !headerIsPortable)
                throw new InvalidDataException("Electron onboarding entry failed integrity verification: " +
                    target.Entry.Path);

            if (!isPortable || (target.ContainsDefaultMessages && !gateIsPortable))
                archive.WriteEntry(target.Entry, portableBytes);
            if (!headerIsPortable) archive.AddIntegrityReplacement(target.Entry, portableIntegrity);
        }

        private static bool HasStandardOnboardingGateState(byte[] bytes, bool portable)
        {
            int official = CountIdentifierPattern(bytes, OfficialStandardOnboardingGateText, null);
            int patched = CountIdentifierPattern(bytes, PortableStandardOnboardingGateText, null);
            return portable ? official == 0 && patched == 1 : official == 1 && patched == 0;
        }

        private static List<OnboardingLiteralTarget> AnalyzeOnboardingEntry(byte[] bytes,
            OnboardingEntryTarget entryTarget)
        {
            List<OnboardingLiteralTarget> result = new List<OnboardingLiteralTarget>();
            result.Add(FindOnboardingLiteral(bytes, "roleOnlyWelcomeIntroduction",
                entryTarget.ContainsDefaultMessages));
            result.Add(FindOnboardingLiteral(bytes, "welcomeIntroduction",
                entryTarget.ContainsDefaultMessages));
            return result;
        }

        private static OnboardingLiteralTarget FindOnboardingLiteral(byte[] bytes, string key,
            bool containsDefaultMessages)
        {
            string id = OnboardingMessageIdPrefix + key;
            string officialPrefix;
            string portablePrefix;
            if (containsDefaultMessages)
            {
                string common = key + ":{id:`" + id + "`,defaultMessage:";
                officialPrefix = common + "`";
                portablePrefix = common + "  `";
            }
            else
            {
                string common = "\"" + id + "\":";
                officialPrefix = common + "`";
                portablePrefix = common + "  `";
            }

            byte[] officialPrefixBytes = Encoding.UTF8.GetBytes(officialPrefix);
            byte[] portablePrefixBytes = Encoding.UTF8.GetBytes(portablePrefix);
            int officialCount = CountPattern(bytes, officialPrefixBytes);
            int portableCount = CountPattern(bytes, portablePrefixBytes);
            if (officialCount + portableCount != 1)
                throw new InvalidDataException("Electron onboarding message target is missing or ambiguous: " + id);

            bool isPortable = portableCount == 1;
            byte[] selectedPrefix = isPortable ? portablePrefixBytes : officialPrefixBytes;
            int prefixOffset = FindPattern(bytes, selectedPrefix, 0, bytes.Length);
            int openingTickOffset = prefixOffset + selectedPrefix.Length - 1;
            int closingTickOffset = FindTemplateLiteralEnd(bytes, openingTickOffset);
            byte[] officialBrand = Encoding.UTF8.GetBytes(OfficialOnboardingBrandText);
            byte[] portableBrand = Encoding.UTF8.GetBytes(PortableOnboardingBrandText);
            int officialBrandCount = CountPattern(bytes, officialBrand,
                openingTickOffset + 1, closingTickOffset);
            int portableBrandCount = CountPattern(bytes, portableBrand,
                openingTickOffset + 1, closingTickOffset);
            if ((!isPortable && (officialBrandCount != 1 || portableBrandCount != 0)) ||
                (isPortable && (officialBrandCount != 0 || portableBrandCount != 1)))
                throw new InvalidDataException("Electron onboarding message brand is invalid: " + id);

            OnboardingLiteralTarget result = new OnboardingLiteralTarget();
            result.IsPortable = isPortable;
            result.OpeningTickOffset = openingTickOffset;
            result.BrandOffset = FindPattern(bytes, isPortable ? portableBrand : officialBrand,
                openingTickOffset + 1, closingTickOffset);
            return result;
        }

        private static int FindTemplateLiteralEnd(byte[] bytes, int openingTickOffset)
        {
            if (openingTickOffset < 0 || openingTickOffset >= bytes.Length || bytes[openingTickOffset] != 0x60)
                throw new InvalidDataException("Electron onboarding message literal is invalid.");
            bool escaped = false;
            for (int i = openingTickOffset + 1; i < bytes.Length; i++)
            {
                byte value = bytes[i];
                if (escaped) { escaped = false; continue; }
                if (value == 0x5c) { escaped = true; continue; }
                if (value == 0x24 && i + 1 < bytes.Length && bytes[i + 1] == 0x7b)
                    throw new InvalidDataException("Electron onboarding message must be a static template literal.");
                if (value == 0x60) return i;
            }
            throw new InvalidDataException("Electron onboarding message literal is unterminated.");
        }

        private static void ConvertOnboardingLiteral(byte[] bytes, OnboardingLiteralTarget target,
            bool makePortable)
        {
            byte[] officialBrand = Encoding.UTF8.GetBytes(OfficialOnboardingBrandText);
            byte[] portableBrand = Encoding.UTF8.GetBytes(PortableOnboardingBrandText);
            if (officialBrand.Length - portableBrand.Length != OnboardingBrandPaddingLength)
                throw new InvalidDataException("Electron onboarding brand replacement length is invalid.");

            if (makePortable)
            {
                if (target.IsPortable || target.BrandOffset < target.OpeningTickOffset + 1 ||
                    target.BrandOffset + officialBrand.Length > bytes.Length)
                    throw new InvalidDataException("Electron onboarding official message bounds are invalid.");
                // Put the two compensating bytes in JavaScript syntax whitespace, outside the displayed value.
                Buffer.BlockCopy(bytes, target.OpeningTickOffset, bytes,
                    target.OpeningTickOffset + OnboardingBrandPaddingLength,
                    target.BrandOffset - target.OpeningTickOffset);
                for (int i = 0; i < OnboardingBrandPaddingLength; i++)
                    bytes[target.OpeningTickOffset + i] = 0x20;
                Buffer.BlockCopy(portableBrand, 0, bytes,
                    target.BrandOffset + OnboardingBrandPaddingLength, portableBrand.Length);
            }
            else
            {
                if (!target.IsPortable || target.OpeningTickOffset < OnboardingBrandPaddingLength ||
                    target.BrandOffset < target.OpeningTickOffset + 1 ||
                    target.BrandOffset + portableBrand.Length > bytes.Length)
                    throw new InvalidDataException("Electron onboarding portable message bounds are invalid.");
                int officialOpeningTickOffset = target.OpeningTickOffset - OnboardingBrandPaddingLength;
                Buffer.BlockCopy(bytes, target.OpeningTickOffset, bytes, officialOpeningTickOffset,
                    target.BrandOffset - target.OpeningTickOffset);
                Buffer.BlockCopy(officialBrand, 0, bytes,
                    target.BrandOffset - OnboardingBrandPaddingLength, officialBrand.Length);
            }
        }

        private static void ValidateOnboardingState(byte[] bytes, OnboardingEntryTarget target,
            bool expectedPortable)
        {
            List<OnboardingLiteralTarget> literals = AnalyzeOnboardingEntry(bytes, target);
            if (literals.Count != 2 || literals[0].IsPortable != expectedPortable ||
                literals[1].IsPortable != expectedPortable)
                throw new InvalidDataException("Electron onboarding message transformation failed: " +
                    target.Entry.Path);
        }

        private static int EnsureDirectClosePattern(AsarArchive archive, AsarEntry entry)
        {
            // A single minified entry can carry more than one independent
            // replacement (for example the update menu and runtime guard).
            // Commit an earlier entry transformation before validating the
            // next one, otherwise its current bytes would not match the
            // still-original integrity header.
            archive.FlushHeader();
            byte[] official = Encoding.UTF8.GetBytes(OfficialCloseToTrayText);
            byte[] legacyPortable = Encoding.UTF8.GetBytes(LegacyPortableCloseToTrayText);
            byte[] portable = Encoding.UTF8.GetBytes(PortableCloseToTrayText);
            byte[] electronAlias = Encoding.UTF8.GetBytes(PortableCloseElectronAliasText);
            if (official.Length != legacyPortable.Length || official.Length != portable.Length)
                throw new InvalidDataException("Electron direct-close replacements must preserve entry length.");

            byte[] currentBytes = archive.ReadEntry(entry);
            int officialCount = CountPattern(currentBytes, official);
            int legacyPortableCount = CountPattern(currentBytes, legacyPortable);
            int portableCount = CountPattern(currentBytes, portable);
            int stateCount = officialCount + legacyPortableCount + portableCount;
            if (stateCount == 0) return 0;
            if (officialCount > 1 || legacyPortableCount > 1 || portableCount > 1 || stateCount != 1)
                throw new InvalidDataException("Electron direct-close target is mixed or ambiguous: " + entry.Path);
            if (CountPattern(currentBytes, electronAlias) != 1)
                throw new InvalidDataException("Electron direct-close app alias is missing or ambiguous: " + entry.Path);

            byte[] originalBytes;
            byte[] legacyPortableBytes;
            byte[] portableBytes;
            if (officialCount == 1)
            {
                originalBytes = currentBytes;
                legacyPortableBytes = (byte[])currentBytes.Clone();
                portableBytes = (byte[])currentBytes.Clone();
                ReplacePattern(legacyPortableBytes, official, legacyPortable);
                ReplacePattern(portableBytes, official, portable);
            }
            else if (legacyPortableCount == 1)
            {
                legacyPortableBytes = currentBytes;
                originalBytes = (byte[])currentBytes.Clone();
                portableBytes = (byte[])currentBytes.Clone();
                ReplacePattern(originalBytes, legacyPortable, official);
                ReplacePattern(portableBytes, legacyPortable, portable);
            }
            else
            {
                portableBytes = currentBytes;
                originalBytes = (byte[])currentBytes.Clone();
                legacyPortableBytes = (byte[])currentBytes.Clone();
                ReplacePattern(originalBytes, portable, official);
                ReplacePattern(legacyPortableBytes, portable, legacyPortable);
            }

            if (CountPattern(originalBytes, official) != 1 ||
                CountPattern(originalBytes, legacyPortable) != 0 || CountPattern(originalBytes, portable) != 0 ||
                CountPattern(legacyPortableBytes, official) != 0 ||
                CountPattern(legacyPortableBytes, legacyPortable) != 1 ||
                CountPattern(legacyPortableBytes, portable) != 0 ||
                CountPattern(portableBytes, official) != 0 ||
                CountPattern(portableBytes, legacyPortable) != 0 || CountPattern(portableBytes, portable) != 1)
                throw new InvalidDataException("Electron direct-close transformation failed: " + entry.Path);

            IntegrityState originalIntegrity = ComputeIntegrity(originalBytes, entry.BlockSize);
            IntegrityState legacyPortableIntegrity = ComputeIntegrity(legacyPortableBytes, entry.BlockSize);
            IntegrityState portableIntegrity = ComputeIntegrity(portableBytes, entry.BlockSize);
            bool headerIsOriginal = IntegrityMatches(entry, originalIntegrity);
            bool headerIsLegacyPortable = IntegrityMatches(entry, legacyPortableIntegrity);
            bool headerIsPortable = IntegrityMatches(entry, portableIntegrity);
            if (!headerIsOriginal && !headerIsLegacyPortable && !headerIsPortable)
                throw new InvalidDataException("Electron direct-close entry failed integrity verification: " + entry.Path);

            if (portableCount != 1) archive.WriteEntry(entry, portableBytes);
            if (!headerIsPortable) archive.AddIntegrityReplacement(entry, portableIntegrity);
            return 1;
        }

        private static int VerifyArchiveIntegrityAndCountJavaScriptPattern(
            AsarArchive archive, string text)
        {
            byte[] pattern = Encoding.UTF8.GetBytes(text);
            int occurrences = 0;
            for (int i = 0; i < archive.Entries.Count; i++)
            {
                AsarEntry entry = archive.Entries[i];
                byte[] bytes = archive.ReadEntry(entry);
                if (!IntegrityMatches(entry, ComputeIntegrity(bytes, entry.BlockSize)))
                    throw new InvalidDataException(
                        "Electron ASAR entry failed integrity verification: " + entry.Path);
                if (entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    occurrences += CountIdentifierPattern(bytes, text, entry);
            }
            return occurrences;
        }

        private static void VerifyArchiveJavaScriptPatternState(AsarArchive archive,
            string officialText, string portableText, int expectedOccurrences, string errorMessage)
        {
            if (expectedOccurrences < 1)
                throw new InvalidDataException("Electron ASAR expected occurrence count is invalid.");
            byte[] official = Encoding.UTF8.GetBytes(officialText);
            byte[] portable = Encoding.UTF8.GetBytes(portableText);
            if (official.Length != portable.Length)
                throw new InvalidDataException("Portable ASAR replacements must preserve entry length.");
            int officialOccurrences = 0;
            int portableOccurrences = 0;
            for (int i = 0; i < archive.Entries.Count; i++)
            {
                AsarEntry entry = archive.Entries[i];
                if (!entry.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] bytes = archive.ReadEntry(entry);
                if (!IntegrityMatches(entry, ComputeIntegrity(bytes, entry.BlockSize)))
                    throw new InvalidDataException(
                        "Electron ASAR entry failed integrity verification: " + entry.Path);
                officialOccurrences += CountPattern(bytes, official);
                portableOccurrences += CountPattern(bytes, portable);
            }
            if (!((officialOccurrences == expectedOccurrences && portableOccurrences == 0) ||
                (officialOccurrences == 0 && portableOccurrences == expectedOccurrences)))
                throw new InvalidDataException(errorMessage);
        }

        private static int EnsurePattern(AsarArchive archive, AsarEntry entry, string officialText,
            string portableText, int maximumOccurrences = 1, bool allowIdentifierFallback = false)
        {
            // Multiple fixed-length patches may share one ASAR entry.  Flush
            // pending integrity replacements before reading it so each patch
            // validates against the header that describes the bytes on disk.
            archive.FlushHeader();
            if (maximumOccurrences < 1)
                throw new InvalidDataException("Electron ASAR patch occurrence limit is invalid.");
            byte[] official = Encoding.UTF8.GetBytes(officialText);
            byte[] portable = Encoding.UTF8.GetBytes(portableText);
            if (official.Length != portable.Length)
                throw new InvalidDataException("Portable ASAR replacements must preserve entry length.");
            byte[] bytes = archive.ReadEntry(entry);
            int officialCount = CountPattern(bytes, official);
            int portableCount = CountPattern(bytes, portable);
            if (officialCount == 0 && portableCount == 0)
            {
                return allowIdentifierFallback ? EnsureIdentifierPatternFallback(archive, entry, bytes,
                    officialText, portableText, maximumOccurrences) : 0;
            }
            if (officialCount > maximumOccurrences || portableCount > maximumOccurrences)
                throw new InvalidDataException("Electron ASAR patch target is ambiguous: " + entry.Path +
                    " for pattern " + ExcerptPattern(officialText) + " (official=" +
                    officialCount.ToString(CultureInfo.InvariantCulture) + ", portable=" +
                    portableCount.ToString(CultureInfo.InvariantCulture) + ")");
            if (officialCount != 0 && portableCount != 0)
                throw new InvalidDataException("Electron ASAR patch state is mixed for pattern " +
                    ExcerptPattern(officialText) + " (official=" +
                    officialCount.ToString(CultureInfo.InvariantCulture) + ", portable=" +
                    portableCount.ToString(CultureInfo.InvariantCulture) + ").");

            IntegrityState current = ComputeIntegrity(bytes, entry.BlockSize);
            if (officialCount != 0)
            {
                ReplacePattern(bytes, official, portable);
                IntegrityState patched = ComputeIntegrity(bytes, entry.BlockSize);
                if (IntegrityMatches(entry, current))
                {
                    archive.WriteEntry(entry, bytes);
                    archive.AddIntegrityReplacement(entry, patched);
                }
                else if (IntegrityMatches(entry, patched))
                {
                    archive.WriteEntry(entry, bytes);
                }
                else throw new InvalidDataException("Electron ASAR entry failed integrity verification: " + entry.Path);
            }
            else if (!IntegrityMatches(entry, current))
            {
                byte[] restored = (byte[])bytes.Clone();
                ReplacePattern(restored, portable, official);
                IntegrityState original = ComputeIntegrity(restored, entry.BlockSize);
                if (!IntegrityMatches(entry, original))
                    throw new InvalidDataException("Electron ASAR entry contains unrecognized changes: " + entry.Path);
                archive.AddIntegrityReplacement(entry, current);
            }
            return officialCount != 0 ? officialCount : portableCount;
        }

        private static int EnsureIdentifierPatternFallback(AsarArchive archive, AsarEntry entry,
            byte[] bytes, string officialText, string portableText, int maximumOccurrences)
        {
            if (!IsIdentifierFallbackPathAllowed(entry, officialText)) return 0;
            UTF8Encoding utf8 = new UTF8Encoding(false, true);
            string text = utf8.GetString(bytes);
            IdentifierPattern officialPattern = BuildIdentifierPattern(officialText);
            MatchCollection officialMatches = officialPattern.Regex.Matches(text);
            IdentifierPattern portablePattern = BuildIdentifierPattern(portableText);
            MatchCollection portableMatches = portablePattern.Regex.Matches(text);
            int portableCount = portableMatches.Count;
            if (officialMatches.Count == 0 && portableCount == 0) return 0;
            if (officialMatches.Count > maximumOccurrences || portableCount > maximumOccurrences)
                throw new InvalidDataException("Electron ASAR semantic patch target is ambiguous: " + entry.Path +
                    " for pattern " + ExcerptPattern(officialText) + " (official=" +
                    officialMatches.Count.ToString(CultureInfo.InvariantCulture) + ", portable=" +
                    portableMatches.Count.ToString(CultureInfo.InvariantCulture) + ")");
            if (officialMatches.Count != 0 && portableCount != 0)
                throw new InvalidDataException("Electron ASAR semantic patch state is mixed: " + entry.Path +
                    " for pattern " + ExcerptPattern(officialText) + " (official=" +
                    officialMatches.Count.ToString(CultureInfo.InvariantCulture) + ", portable=" +
                    portableMatches.Count.ToString(CultureInfo.InvariantCulture) + ")");

            IntegrityState current = ComputeIntegrity(bytes, entry.BlockSize);
            if (officialMatches.Count != 0)
            {
                byte[] patched = RewriteIdentifierMatches(text, bytes.Length, officialMatches,
                    officialPattern, portableText, utf8);
                IntegrityState patchedIntegrity = ComputeIntegrity(patched, entry.BlockSize);
                if (IntegrityMatches(entry, current))
                {
                    archive.WriteEntry(entry, patched);
                    archive.AddIntegrityReplacement(entry, patchedIntegrity);
                }
                else if (IntegrityMatches(entry, patchedIntegrity)) archive.WriteEntry(entry, patched);
                else throw new InvalidDataException(
                    "Electron ASAR semantic target failed integrity verification: " + entry.Path);
                return officialMatches.Count;
            }

            if (!IntegrityMatches(entry, current))
            {
                byte[] restored = RewriteIdentifierMatches(text, bytes.Length, portableMatches,
                    portablePattern, officialText, utf8);
                IntegrityState original = ComputeIntegrity(restored, entry.BlockSize);
                if (!IntegrityMatches(entry, original))
                    throw new InvalidDataException(
                        "Electron ASAR semantic target contains unrecognized changes: " + entry.Path);
                archive.AddIntegrityReplacement(entry, current);
            }
            return portableCount;
        }

        private static string ExcerptPattern(string text)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            string value = text;
            if (value.Length > 72) value = value.Substring(0, 72) + "…";
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i])) value = value.Substring(0, i) + "…";
            return value;
        }

        private static bool IsIdentifierFallbackPathAllowed(AsarEntry entry, string officialText)
        {
            string path = entry.Path ?? "";
            if (string.Equals(officialText, OfficialBrandText, StringComparison.Ordinal))
                return path.IndexOf(".vite/build/window-all-closed", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.Equals(officialText, WorkspaceDependenciesSettingsFunctionText,
                    StringComparison.Ordinal))
                return path.IndexOf("agent-settings", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.Equals(officialText, OfficialSparkleGateText, StringComparison.Ordinal))
                return path.IndexOf(".vite/build/file-based-logger", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.Equals(officialText, OfficialWorkerSparkleGateText, StringComparison.Ordinal))
                return path.EndsWith(".vite/build/worker.js", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(officialText, OfficialSunsetUpdateGateText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialTryModelAvailabilityGateText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialTryModelUpgradeGateText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialWindowsSandboxSetupPendingGateText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialWindowsSandboxComposerStateText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialWindowsSandboxReadinessStateText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialBrowserPluginAvailabilityText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialChromePluginAvailabilityText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialComputerUsePluginAvailabilityText, StringComparison.Ordinal))
                return path.IndexOf("app-initial", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.Equals(officialText, OfficialBrowserPluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialChromePluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialComputerUsePluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialSitesPluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialDeepResearchPluginReconcileAvailabilityText,
                    StringComparison.Ordinal))
                return path.StartsWith(BuildJavaScriptPrefix + "main-",
                    StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(officialText, OfficialStandardOnboardingGateText, StringComparison.Ordinal) ||
                string.Equals(officialText, OfficialWindowsSandboxFinalStepText, StringComparison.Ordinal))
                return path.IndexOf("onboarding-page", StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }

        private static byte[] RewriteIdentifierMatches(string text, int expectedByteLength,
            MatchCollection matches, IdentifierPattern sourcePattern, string replacementTemplate,
            UTF8Encoding utf8)
        {
            StringBuilder rewritten = new StringBuilder(text);
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                Match match = matches[i];
                // Carry the aliases captured from the actual bundle into the
                // replacement.  The minifier may rename a function or its
                // parameters between desktop releases; using the constants'
                // old aliases would leave a syntactically valid but broken
                // declaration.
                string replacement = RenderIdentifierTemplate(replacementTemplate,
                    sourcePattern, match);
                if (replacement.Length > match.Length)
                    throw new InvalidDataException(
                        "Electron ASAR semantic replacement exceeds its target.");
                replacement = replacement.PadRight(match.Length);
                rewritten.Remove(match.Index, match.Length);
                rewritten.Insert(match.Index, replacement);
            }
            byte[] result = utf8.GetBytes(rewritten.ToString());
            if (result.Length != expectedByteLength)
                throw new InvalidDataException(
                    "Electron ASAR semantic replacement changed entry length.");
            return result;
        }

        private static IdentifierPattern BuildIdentifierPattern(string template)
        {
            lock (IdentifierPatternCacheLock)
            {
                IdentifierPattern cached;
                if (IdentifierPatternCache.TryGetValue(template, out cached)) return cached;
            }
            IdentifierPattern result = new IdentifierPattern();
            StringBuilder pattern = new StringBuilder();
            string source = template.TrimEnd(' ');
            char quote = '\0';
            bool escaped = false;
            for (int i = 0; i < source.Length; )
            {
                char value = source[i];
                if (quote != '\0')
                {
                    pattern.Append(Regex.Escape(value.ToString()));
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (value == '\'' || value == '"' || value == '`')
                {
                    quote = value;
                    pattern.Append(Regex.Escape(value.ToString()));
                    i++;
                    continue;
                }
                if (!IsJavaScriptIdentifierStart(value))
                {
                    pattern.Append(Regex.Escape(value.ToString()));
                    i++;
                    continue;
                }

                int start = i++;
                while (i < source.Length && IsJavaScriptIdentifierPart(source[i])) i++;
                string identifier = source.Substring(start, i - start);
                if (!IsVolatileJavaScriptIdentifier(identifier))
                {
                    pattern.Append(Regex.Escape(identifier));
                    continue;
                }
                string group;
                if (!result.Groups.TryGetValue(identifier, out group))
                {
                    group = "v" + result.Groups.Count.ToString(CultureInfo.InvariantCulture);
                    result.Groups.Add(identifier, group);
                    pattern.Append("(?<![A-Za-z0-9_$])(?<").Append(group).
                        Append(">[A-Za-z_$][A-Za-z0-9_$]*)(?![A-Za-z0-9_$])");
                }
                else
                {
                    pattern.Append("(?<![A-Za-z0-9_$])\\k<").Append(group).
                        Append(">(?![A-Za-z0-9_$])");
                }
            }
            pattern.Append(" *");
            result.Regex = new Regex(pattern.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Compiled);
            lock (IdentifierPatternCacheLock)
            {
                IdentifierPattern cached;
                if (IdentifierPatternCache.TryGetValue(template, out cached)) return cached;
                IdentifierPatternCache.Add(template, result);
            }
            return result;
        }

        private static string RenderIdentifierTemplate(string template, IdentifierPattern sourcePattern,
            Match sourceMatch)
        {
            StringBuilder result = new StringBuilder();
            string source = template.TrimEnd(' ');
            char quote = '\0';
            bool escaped = false;
            for (int i = 0; i < source.Length; )
            {
                char value = source[i];
                if (quote != '\0')
                {
                    result.Append(value);
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (value == '\'' || value == '"' || value == '`')
                {
                    quote = value;
                    result.Append(value);
                    i++;
                    continue;
                }
                if (!IsJavaScriptIdentifierStart(value))
                {
                    result.Append(value);
                    i++;
                    continue;
                }
                int start = i++;
                while (i < source.Length && IsJavaScriptIdentifierPart(source[i])) i++;
                string identifier = source.Substring(start, i - start);
                string group;
                if (IsVolatileJavaScriptIdentifier(identifier) &&
                          sourcePattern.Groups.TryGetValue(identifier, out group) &&
                          sourceMatch.Groups[group].Success)
                    result.Append(sourceMatch.Groups[group].Value);
                else result.Append(identifier);
            }
            return result.ToString();
        }

        private static bool IsJavaScriptIdentifierStart(char value)
        {
            return value == '$' || value == '_' ||
                value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';
        }

        private static bool IsJavaScriptIdentifierPart(char value)
        {
            return IsJavaScriptIdentifierStart(value) || value >= '0' && value <= '9';
        }

        private static bool IsVolatileJavaScriptIdentifier(string value)
        {
            if (value.Length > 3) return false;
            switch (value)
            {
                case "as": case "do": case "for": case "get": case "if": case "in":
                case "let": case "new": case "of": case "set": case "try": case "var":
                    return false;
                default:
                    return true;
            }
        }

        private static byte[] RewriteIdentifierPattern(byte[] bytes, string officialText,
            string portableText)
        {
            byte[] official = Encoding.UTF8.GetBytes(officialText);
            byte[] portable = Encoding.UTF8.GetBytes(portableText);
            if (CountPattern(bytes, official) == 1)
            {
                byte[] exactResult = (byte[])bytes.Clone();
                ReplacePattern(exactResult, official, portable);
                return exactResult;
            }
            UTF8Encoding utf8 = new UTF8Encoding(false, true);
            string text = utf8.GetString(bytes);
            IdentifierPattern pattern = BuildIdentifierPattern(officialText);
            MatchCollection matches = pattern.Regex.Matches(text);
            if (matches.Count != 1)
                throw new InvalidDataException(
                    "Electron ASAR semantic replacement target is missing or ambiguous.");
            return RewriteIdentifierMatches(text, bytes.Length, matches, pattern, portableText, utf8);
        }

        private static int CountIdentifierPattern(byte[] bytes, string exactText, AsarEntry entry)
        {
            byte[] exact = Encoding.UTF8.GetBytes(exactText);
            int count = CountPattern(bytes, exact);
            if (count != 0) return count;
            string officialText = GetIdentifierFallbackOfficialText(exactText);
            if (officialText == null || entry != null &&
                !IsIdentifierFallbackPathAllowed(entry, officialText)) return 0;
            string text = new UTF8Encoding(false, true).GetString(bytes);
            return BuildIdentifierPattern(exactText).Regex.Matches(text).Count;
        }

        private static string GetIdentifierFallbackOfficialText(string text)
        {
            if (string.Equals(text, OfficialBrandText, StringComparison.Ordinal) ||
                string.Equals(text, PortableBrandText, StringComparison.Ordinal)) return OfficialBrandText;
            if (string.Equals(text, WorkspaceDependenciesSettingsFunctionText,
                    StringComparison.Ordinal)) return WorkspaceDependenciesSettingsFunctionText;
            if (string.Equals(text, OfficialSparkleGateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableSparkleGateText, StringComparison.Ordinal)) return OfficialSparkleGateText;
            if (string.Equals(text, OfficialWorkerSparkleGateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableWorkerSparkleGateText, StringComparison.Ordinal))
                return OfficialWorkerSparkleGateText;
            if (string.Equals(text, OfficialSunsetUpdateGateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableSunsetUpdateGateText, StringComparison.Ordinal))
                return OfficialSunsetUpdateGateText;
            if (string.Equals(text, OfficialTryModelAvailabilityGateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableTryModelAvailabilityGateText, StringComparison.Ordinal))
                return OfficialTryModelAvailabilityGateText;
            if (string.Equals(text, OfficialTryModelUpgradeGateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableTryModelUpgradeGateText, StringComparison.Ordinal))
                return OfficialTryModelUpgradeGateText;
            if (string.Equals(text, OfficialWindowsSandboxComposerStateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableWindowsSandboxComposerStateText, StringComparison.Ordinal))
                return OfficialWindowsSandboxComposerStateText;
            if (string.Equals(text, OfficialWindowsSandboxReadinessStateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableWindowsSandboxReadinessStateText, StringComparison.Ordinal))
                return OfficialWindowsSandboxReadinessStateText;
            if (string.Equals(text, OfficialWindowsSandboxSetupPendingGateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableWindowsSandboxSetupPendingGateText, StringComparison.Ordinal))
                return OfficialWindowsSandboxSetupPendingGateText;
            if (string.Equals(text, OfficialWindowsSandboxFinalStepText, StringComparison.Ordinal) ||
                string.Equals(text, PortableWindowsSandboxFinalStepText, StringComparison.Ordinal))
                return OfficialWindowsSandboxFinalStepText;
            if (string.Equals(text, OfficialStandardOnboardingGateText, StringComparison.Ordinal) ||
                string.Equals(text, PortableStandardOnboardingGateText, StringComparison.Ordinal))
                return OfficialStandardOnboardingGateText;
            if (string.Equals(text, OfficialBrowserPluginAvailabilityText, StringComparison.Ordinal) ||
                string.Equals(text, PortableBrowserPluginAvailabilityText, StringComparison.Ordinal))
                return OfficialBrowserPluginAvailabilityText;
            if (string.Equals(text, OfficialChromePluginAvailabilityText, StringComparison.Ordinal) ||
                string.Equals(text, PortableChromePluginAvailabilityText, StringComparison.Ordinal))
                return OfficialChromePluginAvailabilityText;
            if (string.Equals(text, OfficialComputerUsePluginAvailabilityText, StringComparison.Ordinal) ||
                string.Equals(text, PortableComputerUsePluginAvailabilityText, StringComparison.Ordinal))
                return OfficialComputerUsePluginAvailabilityText;
            if (string.Equals(text, OfficialBrowserPluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(text, PortableBrowserPluginReconcileAvailabilityText,
                    StringComparison.Ordinal))
                return OfficialBrowserPluginReconcileAvailabilityText;
            if (string.Equals(text, OfficialChromePluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(text, PortableChromePluginReconcileAvailabilityText,
                    StringComparison.Ordinal))
                return OfficialChromePluginReconcileAvailabilityText;
            if (string.Equals(text, OfficialComputerUsePluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(text, PortableComputerUsePluginReconcileAvailabilityText,
                    StringComparison.Ordinal))
                return OfficialComputerUsePluginReconcileAvailabilityText;
            if (string.Equals(text, OfficialSitesPluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(text, PortableSitesPluginReconcileAvailabilityText,
                    StringComparison.Ordinal))
                return OfficialSitesPluginReconcileAvailabilityText;
            if (string.Equals(text, OfficialDeepResearchPluginReconcileAvailabilityText,
                    StringComparison.Ordinal) ||
                string.Equals(text, PortableDeepResearchPluginReconcileAvailabilityText,
                    StringComparison.Ordinal))
                return OfficialDeepResearchPluginReconcileAvailabilityText;
            return null;
        }

        private static IntegrityState ComputeIntegrity(byte[] bytes, int blockSize)
        {
            IntegrityState result = new IntegrityState();
            using (SHA256 sha = SHA256.Create()) result.Hash = ToHex(sha.ComputeHash(bytes));
            if (bytes.Length == 0)
            {
                result.Blocks.Add(result.Hash);
                return result;
            }
            for (int offset = 0; offset < bytes.Length; offset += blockSize)
            {
                int count = Math.Min(blockSize, bytes.Length - offset);
                using (SHA256 sha = SHA256.Create()) result.Blocks.Add(ToHex(sha.ComputeHash(bytes, offset, count)));
            }
            return result;
        }

        private static bool IntegrityMatches(AsarEntry entry, IntegrityState actual)
        {
            if (!string.Equals(entry.IntegrityHash, actual.Hash, StringComparison.OrdinalIgnoreCase) ||
                entry.IntegrityBlocks.Count != actual.Blocks.Count) return false;
            for (int i = 0; i < entry.IntegrityBlocks.Count; i++)
                if (!string.Equals(entry.IntegrityBlocks[i], actual.Blocks[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static int CountPattern(byte[] bytes, byte[] pattern)
        {
            return CountPattern(bytes, pattern, 0, bytes.Length);
        }

        private static int CountPattern(byte[] bytes, byte[] pattern, int start, int endExclusive)
        {
            if (pattern.Length == 0 || start < 0 || endExclusive < start || endExclusive > bytes.Length)
                throw new ArgumentOutOfRangeException("Invalid byte-pattern search bounds.");
            int count = 0;
            int lastStart = endExclusive - pattern.Length;
            int candidate = start;
            // Searching for the first byte with the framework implementation
            // avoids comparing every long pattern at every byte offset.  The
            // old loop made the ASAR preflight effectively O(bytes * targets).
            while (candidate <= lastStart)
            {
                candidate = Array.IndexOf(bytes, pattern[0], candidate,
                    endExclusive - candidate);
                if (candidate < 0 || candidate > lastStart) break;
                int j = 0;
                while (j < pattern.Length && bytes[candidate + j] == pattern[j]) j++;
                if (j == pattern.Length) { count++; candidate += pattern.Length; }
                else candidate++;
            }
            return count;
        }

        private static int FindPattern(byte[] bytes, byte[] pattern, int start, int endExclusive)
        {
            if (pattern.Length == 0 || start < 0 || endExclusive < start || endExclusive > bytes.Length)
                throw new ArgumentOutOfRangeException("Invalid byte-pattern search bounds.");
            int lastStart = endExclusive - pattern.Length;
            int candidate = start;
            while (candidate <= lastStart)
            {
                candidate = Array.IndexOf(bytes, pattern[0], candidate,
                    endExclusive - candidate);
                if (candidate < 0 || candidate > lastStart) return -1;
                int j = 0;
                while (j < pattern.Length && bytes[candidate + j] == pattern[j]) j++;
                if (j == pattern.Length) return candidate;
                candidate++;
            }
            return -1;
        }

        private static void ReplacePattern(byte[] bytes, byte[] original, byte[] replacement)
        {
            if (original.Length != replacement.Length) throw new ArgumentException("Pattern lengths differ.");
            for (int i = 0; i <= bytes.Length - original.Length; )
            {
                int j = 0;
                while (j < original.Length && bytes[i + j] == original[j]) j++;
                if (j != original.Length) { i++; continue; }
                Buffer.BlockCopy(replacement, 0, bytes, i, replacement.Length);
                i += replacement.Length;
            }
        }

        private static string NormalizeHash(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length != 64) throw new InvalidDataException("Electron ASAR SHA-256 hash is invalid.");
            for (int i = 0; i < value.Length; i++)
                if (!Uri.IsHexDigit(value[i])) throw new InvalidDataException("Electron ASAR SHA-256 hash is invalid.");
            return value.ToLowerInvariant();
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read == 0) throw new EndOfStreamException("Electron ASAR is truncated.");
                offset += read;
                count -= read;
            }
        }
    }

    internal static class LauncherLocale
    {
        private static bool chinese;

        internal static bool IsChinese { get { return chinese; } }

        internal static void Load(PortableLayout layout)
        {
            string saved = null;
            try
            {
                if (File.Exists(layout.LanguageFile))
                    saved = File.ReadAllText(layout.LanguageFile, Encoding.UTF8).Trim();
            }
            catch { }

            if (string.Equals(saved, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(saved, "zh", StringComparison.OrdinalIgnoreCase))
            {
                chinese = true;
                return;
            }
            if (string.Equals(saved, "en-US", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(saved, "en", StringComparison.OrdinalIgnoreCase))
            {
                chinese = false;
                return;
            }

            CultureInfo culture = CultureInfo.CurrentUICulture;
            if (culture == null || string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
                culture = CultureInfo.InstalledUICulture;
            chinese = culture != null &&
                string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
        }

        internal static void Save(PortableLayout layout, bool useChinese)
        {
            layout.EnsureDirectories();
            IOUtil.AtomicWriteText(layout.LanguageFile, useChinese ? "zh-CN\r\n" : "en-US\r\n");
            chinese = useChinese;
        }

        internal static string T(string zh, string en)
        {
            return chinese ? zh : en;
        }
    }

    internal sealed class PortableForm : Form
    {
        private const int StartupInitializationStepTotal = 4;
        private const int DesktopStartupConfirmationMilliseconds = 9000;
        private const int DesktopStartupIdleProbeMilliseconds = 2000;
        private const int DesktopStartupIdleGraceMilliseconds = 1000;
        private readonly PortableLayout layout;
        private readonly Label status;
        private readonly Label details;
        private readonly ProgressBar progress;
        private readonly Label progressText;
        private readonly CheckBox compatibility;
        private readonly List<Button> actionButtons;
        private Label actionsTitleLabel;
        private Label languageLabel;
        private ComboBox languageSelector;
        private JobRun activeRun;
        private bool busy;
        private bool startWorkflowRunning;
        private bool closingAfterConfirm;
        private bool formIsClosing;
        private bool portablePayloadChecked;
        private bool portablePayloadPreflight;
        private bool startupStatePrepared;
        private bool startupInitializationRunning;
        private bool requiredPluginCacheValidated;
        private readonly bool autoStart;
        private Task<StartupInitialization> startupTask;
        private bool closeRequestedDuringStartup;
        private bool updatingLanguage;
        private bool launchNeedsCommonRuntime;
        private bool launchNeedsDesktopPackage;
        private int launchStepTotal;

        private sealed class StartupInitialization
        {
            internal bool SupportedArchitecture;
            internal bool PayloadPresent;
            internal bool BundledPayloadAvailable;
            internal bool PortablePayloadPrepared;
            internal bool ApiConfigured;
        }

        private sealed class DesktopStartupExitException : Exception
        {
            internal readonly uint ExitCode;

            internal DesktopStartupExitException(uint exitCode)
                : base("Codex Desktop exited during startup with status 0x" +
                    exitCode.ToString("X8", CultureInfo.InvariantCulture) + ".")
            {
                ExitCode = exitCode;
            }
        }

        internal PortableForm(PortableLayout p, bool startAutomatically)
        {
            layout = p;
            autoStart = startAutomatically;
            LauncherLocale.Load(layout);
            Text = "LF Portable · Codex";
            Icon = PortableBranding.LoadLauncherIcon();
            ShowIcon = true;
            ShowInTaskbar = true;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            ClientSize = new Size(860, 520);
            BackColor = Color.FromArgb(246, 248, 251);

            // The launcher uses native WinForms controls so opening it remains
            // lightweight on removable media.
            Color ink = Color.FromArgb(15, 23, 42);
            Color muted = Color.FromArgb(100, 116, 139);
            Color railColor = Color.FromArgb(15, 29, 48);
            Color accent = Color.FromArgb(94, 234, 212);
            Color surface = Color.White;
            Color border = Color.FromArgb(220, 228, 238);
            int contentLeft = 252;
            int contentWidth = 576;

            Panel rail = new Panel();
            rail.Location = new Point(0, 0);
            rail.Size = new Size(220, ClientSize.Height);
            rail.BackColor = railColor;
            Controls.Add(rail);

            Panel railAccent = new Panel();
            railAccent.Location = new Point(0, 0);
            railAccent.Size = new Size(4, ClientSize.Height);
            railAccent.BackColor = accent;
            rail.Controls.Add(railAccent);

            PictureBox brandMark = new PictureBox();
            brandMark.Location = new Point(30, 34);
            brandMark.Size = new Size(62, 62);
            brandMark.BackColor = Color.Transparent;
            brandMark.SizeMode = PictureBoxSizeMode.Zoom;
            using (Icon launcherIcon = PortableBranding.LoadLauncherIcon())
            {
                brandMark.Image = launcherIcon.ToBitmap();
            }
            rail.Controls.Add(brandMark);

            Label railTitle = new Label();
            railTitle.Text = "LF";
            railTitle.Font = new Font(Font.FontFamily, 24F, FontStyle.Bold);
            railTitle.ForeColor = Color.White;
            railTitle.AutoSize = true;
            railTitle.Location = new Point(30, 119);
            rail.Controls.Add(railTitle);

            languageLabel = new Label();
            languageLabel.Text = LauncherLocale.T("界面语言", "Language");
            languageLabel.Font = new Font(Font.FontFamily, 8F, FontStyle.Bold);
            languageLabel.ForeColor = Color.FromArgb(183, 196, 211);
            languageLabel.AutoSize = true;
            languageLabel.Location = new Point(32, 420);
            rail.Controls.Add(languageLabel);

            languageSelector = new ComboBox();
            languageSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            languageSelector.FlatStyle = FlatStyle.Flat;
            languageSelector.Font = new Font(Font.FontFamily, 9F);
            languageSelector.Items.Add("中文");
            languageSelector.Items.Add("English");
            languageSelector.SelectedIndex = LauncherLocale.IsChinese ? 0 : 1;
            languageSelector.Location = new Point(32, 442);
            languageSelector.Size = new Size(154, 26);
            languageSelector.SelectedIndexChanged += delegate
            {
                if (updatingLanguage || languageSelector.SelectedIndex < 0) return;
                bool previous = LauncherLocale.IsChinese;
                try
                {
                    LauncherLocale.Save(layout, languageSelector.SelectedIndex == 0);
                    ApplyLanguage();
                }
                catch (Exception ex)
                {
                    SafeLog.TryWrite(layout, "language", ex);
                    updatingLanguage = true;
                    try { languageSelector.SelectedIndex = previous ? 0 : 1; }
                    finally { updatingLanguage = false; }
                    MessageBox.Show(LauncherLocale.T("语言设置无法保存。请检查 U 盘是否可写。", "The language setting could not be saved. Check that the USB drive is writable."),
                        "LF Portable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            rail.Controls.Add(languageSelector);

            Label title = new Label();
            title.Text = "LF Portable";
            title.Font = new Font(Font.FontFamily, 19F, FontStyle.Bold);
            title.ForeColor = ink;
            title.AutoSize = true;
            title.Location = new Point(contentLeft, 42);
            Controls.Add(title);

            Panel statusCard = new Panel();
            statusCard.Location = new Point(contentLeft, 92);
            statusCard.Size = new Size(contentWidth, 104);
            statusCard.BackColor = surface;
            statusCard.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(border))
                    e.Graphics.DrawRectangle(pen, 0, 0, statusCard.Width - 1, statusCard.Height - 1);
                using (Brush brush = new SolidBrush(Color.FromArgb(13, 148, 136)))
                    e.Graphics.FillRectangle(brush, 0, 0, 4, statusCard.Height);
            };
            Controls.Add(statusCard);

            status = new Label();
            status.Text = LauncherLocale.T("检查便携环境", "Checking portable environment");
            status.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            status.ForeColor = ink;
            status.Location = new Point(22, 15);
            status.Size = new Size(contentWidth - 42, 28);
            statusCard.Controls.Add(status);

            details = new Label();
            details.ForeColor = muted;
            details.Location = new Point(22, 47);
            details.Size = new Size(contentWidth - 42, 38);
            details.AutoEllipsis = true;
            statusCard.Controls.Add(details);

            Label actionsTitle = actionsTitleLabel = new Label();
            actionsTitle.Text = LauncherLocale.T("操作", "Actions");
            actionsTitle.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            actionsTitle.ForeColor = muted;
            actionsTitle.AutoSize = true;
            actionsTitle.Location = new Point(contentLeft, 218);
            Controls.Add(actionsTitle);

            actionButtons = new List<Button>();
            AddButton(LauncherLocale.T("启动 Codex", "Start Codex"), contentLeft, 246, StartClicked, true);
            AddButton(LauncherLocale.T("设置 API", "Configure API"), contentLeft + 296, 246, SetKeyClicked, false);
            AddButton(LauncherLocale.T("清除 API", "Clear API"), contentLeft, 298, ClearKeyClicked, false);
            AddButton(LauncherLocale.T("生成诊断", "Create diagnostics"), contentLeft + 296, 298, DiagnosticsClicked, false);
            AddButton(LauncherLocale.T("打开资料目录", "Open data folder"), contentLeft, 350, OpenDataClicked, false);

            compatibility = new CheckBox();
            compatibility.Text = LauncherLocale.T("兼容模式", "Compatibility mode");
            compatibility.ForeColor = muted;
            compatibility.AutoSize = true;
            compatibility.Location = new Point(contentLeft + 296, 417);
            Controls.Add(compatibility);

            progress = new ProgressBar();
            progress.Location = new Point(contentLeft, 480);
            progress.Size = new Size(contentWidth, 7);
            progress.Style = ProgressBarStyle.Continuous;
            Controls.Add(progress);

            progressText = new Label();
            progressText.ForeColor = muted;
            progressText.Location = new Point(contentLeft, 452);
            progressText.Size = new Size(contentWidth, 20);
            progressText.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(progressText);

            FormClosing += FormIsClosing;
            Shown += FormShown;
        }

        private void AddButton(string text, int x, int y, EventHandler handler, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = new Size(280, 44);
            b.Location = new Point(x, y);
            b.Font = new Font(Font.FontFamily, primary ? 10F : 9F, primary ? FontStyle.Bold : FontStyle.Regular);
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Padding = new Padding(16, 0, 8, 0);
            b.Cursor = Cursors.Hand;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.TabStop = false;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            if (primary)
            {
                b.BackColor = Color.FromArgb(13, 148, 136);
                b.ForeColor = Color.White;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(11, 124, 114);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(8, 101, 94);
            }
            else
            {
                b.BackColor = Color.White;
                b.ForeColor = Color.FromArgb(30, 41, 59);
                b.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 245, 249);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 232, 240);
            }
            b.MouseEnter += delegate
            {
                if (!b.Enabled) return;
                b.BackColor = primary ? Color.FromArgb(11, 124, 114) : Color.FromArgb(241, 245, 249);
            };
            b.MouseLeave += delegate
            {
                b.BackColor = primary ? Color.FromArgb(13, 148, 136) : Color.White;
            };
            b.Click += handler;
            Controls.Add(b);
            actionButtons.Add(b);
        }

        private void ApplyLanguage()
        {
            updatingLanguage = true;
            try
            {
                languageLabel.Text = LauncherLocale.T("界面语言", "Language");
                actionsTitleLabel.Text = LauncherLocale.T("操作", "Actions");
                actionButtons[0].Text = LauncherLocale.T("启动 Codex", "Start Codex");
                actionButtons[1].Text = LauncherLocale.T("设置 API", "Configure API");
                actionButtons[2].Text = LauncherLocale.T("清除 API", "Clear API");
                actionButtons[3].Text = LauncherLocale.T("生成诊断", "Create diagnostics");
                actionButtons[4].Text = LauncherLocale.T("打开资料目录", "Open data folder");
                compatibility.Text = LauncherLocale.T("兼容模式", "Compatibility mode");
            }
            finally { updatingLanguage = false; }

            if (!busy)
            {
                if (startupStatePrepared) RefreshStatus(LauncherLocale.T("就绪", "Ready"));
                else status.Text = LauncherLocale.T("检查便携环境", "Checking portable environment");
            }
        }

        private async void FormShown(object sender, EventArgs e)
        {
            if (formIsClosing || busy) return;
            startupInitializationRunning = true;
            SetBusy(true, null);
            ApplyStartupInitializationProgress(0, "检查数据目录", "Checking data directories",
                "创建并验证便携数据目录", "Creating and validating portable data directories");
            try
            {
                // Initialization touches a portable drive and may parse several JSON
                // manifests. Keep the UI responsive while preserving the same
                // validation order on a worker thread.
                startupTask = Task.Run(delegate
                {
                    StartupInitialization result = new StartupInitialization();
                    layout.EnsureDirectories();
                    ReportStartupInitializationProgress(1, "清理旧登录数据", "Cleaning legacy sign-in data",
                        "移除旧版遗留的登录文件", "Removing sign-in files left by older versions");
                    ProviderConfiguration.CleanupLegacyAuthentication(layout);
                    ReportStartupInitializationProgress(2, "检查 Windows 架构", "Checking Windows architecture",
                        "确认当前系统可用的 Codex 程序包", "Finding the Codex package for this system");
                    result.SupportedArchitecture = ArchitectureInfo.HasOfficialDesktopPayload(layout.Architecture);
                    result.BundledPayloadAvailable = result.SupportedArchitecture &&
                        PortableBundle.HasInstallPackages(layout);
                    ReportStartupInitializationProgress(3, "检查 LF 发布包", "Checking LF release package",
                        "确认桌面程序与 API 配置", "Checking the desktop payload and API configuration");
                    // A bundled package is enough to make the launcher usable;
                    // defer the expensive ASAR/branding scan until Start is
                    // clicked. If no package is available, the installed tree
                    // is the only recovery input, so it must still be checked.
                    if (result.SupportedArchitecture)
                    {
                        if (result.BundledPayloadAvailable)
                        {
                            result.PayloadPresent = true;
                        }
                        else
                        {
                            result.PortablePayloadPrepared = PortableBranding.IsPrepared(layout);
                            result.PayloadPresent = result.PortablePayloadPrepared;
                        }
                    }
                    ReportStartupInitializationProgress(4, "检查 API 配置", "Checking API configuration",
                        "确认 API URL、密钥和模型", "Checking the API URL, key, and model");
                    result.ApiConfigured = ProviderConfiguration.HasCompleteApiConfiguration(layout);
                    return result;
                });
                StartupInitialization initialization = await startupTask;
                startupTask = null;
                ApplyStartupInitializationProgress(StartupInitializationStepTotal,
                    "便携环境检查完成", "Portable environment checks complete", string.Empty, string.Empty);
                startupInitializationRunning = false;

                if (formIsClosing || IsDisposed || Disposing) return;
                if (closeRequestedDuringStartup)
                {
                    CloseAfterStartupInitialization();
                    return;
                }
                portablePayloadPreflight = initialization.PortablePayloadPrepared;
                // The background preflight already performed the full ASAR and
                // branding verification. Reuse that result for this launcher's
                // lifetime so the handoff path does not scan the 200+ MiB ASAR
                // a second time. This is an in-memory hint only; any later
                // failure still falls through to the normal repair/error path.
                portablePayloadChecked = initialization.PortablePayloadPrepared;
                // Directory creation and legacy-auth cleanup are complete. The
                // config and onboarding files are intentionally prepared at
                // Start time so opening the launcher does not perform duplicate
                // write-through work before the user chooses an action.
                startupStatePrepared = true;
                SetBusy(false, null);
                if (!initialization.SupportedArchitecture)
                {
                    status.Text = LauncherLocale.T("不支持的 Windows 架构", "Unsupported Windows architecture");
                    details.Text = string.Empty;
                    return;
                }
                if (!initialization.PayloadPresent)
                {
                    status.Text = LauncherLocale.T("Codex Desktop 不完整", "Codex Desktop is incomplete");
                    details.Text = string.Empty;
                    return;
                }
                if (initialization.BundledPayloadAvailable && !portablePayloadPreflight)
                {
                    RefreshStatus(LauncherLocale.T("就绪", "Ready"));
                    ScheduleAutomaticStart(initialization.ApiConfigured);
                    return;
                }
                if (initialization.ApiConfigured)
                {
                    RefreshStatus(LauncherLocale.T("就绪", "Ready"));
                    ScheduleAutomaticStart(true);
                }
                else
                {
                    RefreshStatus(LauncherLocale.T("API 未设置", "API not configured"));
                }
            }
            catch (Exception ex)
            {
                startupTask = null;
                startupInitializationRunning = false;
                if (closeRequestedDuringStartup)
                {
                    CloseAfterStartupInitialization();
                    return;
                }
                if (formIsClosing || IsDisposed || Disposing) return;
                SetBusy(false, null);
                SafeLog.TryWrite(layout, "initialization", ex);
                status.Text = LauncherLocale.T("便携环境检查失败", "Portable environment check failed");
                details.Text = string.Empty;
            }
        }

        private void ScheduleAutomaticStart(bool apiConfigured)
        {
            if (!autoStart || !apiConfigured || formIsClosing || IsDisposed || Disposing) return;
            // FormShown is an async event handler. Queue the click only after
            // the initialization continuation has returned to the UI loop so
            // the normal StartClicked workflow owns all preparation and error
            // handling paths.
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (!formIsClosing && !busy && !startWorkflowRunning && !IsDisposed && !Disposing)
                        StartClicked(this, EventArgs.Empty);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void CloseAfterStartupInitialization()
        {
            try { ProviderConfiguration.CleanupLegacyAuthentication(layout); }
            catch (Exception ex) { SafeLog.TryWrite(layout, "startup-close-cleanup", ex); }
            try { PortableScratch.Cleanup(layout); }
            catch (Exception ex) { SafeLog.TryWrite(layout, "startup-close-scratch", ex); }
            formIsClosing = true;
            closingAfterConfirm = true;
            try { BeginInvoke(new MethodInvoker(delegate { if (!IsDisposed) Close(); })); }
            catch { }
        }

        private void RefreshStatus(string prefix)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            status.Text = prefix;
            details.Text = LauncherLocale.T("启动器版本：" + version, "Launcher version: " + version);
        }

        private void ReportStartupInitializationProgress(int completedSteps, string zhStatus,
            string enStatus, string zhDetails, string enDetails)
        {
            if (!InvokeRequired)
            {
                ApplyStartupInitializationProgress(completedSteps, zhStatus, enStatus,
                    zhDetails, enDetails);
                return;
            }
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    ApplyStartupInitializationProgress(completedSteps, zhStatus, enStatus,
                        zhDetails, enDetails);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyStartupInitializationProgress(int completedSteps, string zhStatus,
            string enStatus, string zhDetails, string enDetails)
        {
            if (!startupInitializationRunning || formIsClosing || IsDisposed || Disposing) return;
            int boundedCompleted = Math.Max(0, Math.Min(StartupInitializationStepTotal, completedSteps));
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = boundedCompleted * 100 / StartupInitializationStepTotal;
            progressText.Text = LauncherLocale.T("已完成 " + boundedCompleted.ToString(
                CultureInfo.InvariantCulture) + "/" + StartupInitializationStepTotal.ToString(
                CultureInfo.InvariantCulture) + " 项",
                boundedCompleted.ToString(CultureInfo.InvariantCulture) + "/" +
                StartupInitializationStepTotal.ToString(CultureInfo.InvariantCulture) +
                " checks complete");
            status.Text = LauncherLocale.T(zhStatus, enStatus);
            details.Text = LauncherLocale.T(zhDetails, enDetails);
            AppendPendingCloseNotice();
        }

        private void AppendPendingCloseNotice()
        {
            if (!closeRequestedDuringStartup) return;
            string currentStage = status.Text ?? string.Empty;
            string notice = currentStage.Length == 0 ?
                LauncherLocale.T("完成当前步骤后自动关闭启动器",
                    "The launcher will close after the current step finishes") :
                LauncherLocale.T("完成“" + currentStage + "”后自动关闭启动器",
                    "The launcher will close after \"" + currentStage + "\" finishes");
            details.Text = string.IsNullOrEmpty(details.Text) ? notice : details.Text + " · " + notice;
        }

        private void SetBusy(bool value, string message)
        {
            busy = value;
            for (int i = 0; i < actionButtons.Count; i++) actionButtons[i].Enabled = !value;
            compatibility.Enabled = !value;
            if (languageSelector != null) languageSelector.Enabled = !value;
            if (message != null) status.Text = message;
            if (!value)
            {
                progress.Style = ProgressBarStyle.Continuous;
                progress.Value = 0;
                progressText.Text = string.Empty;
            }
        }

        private void BeginLaunchProgressPlan(bool needsCommonRuntime, bool needsDesktopPackage)
        {
            launchNeedsCommonRuntime = needsCommonRuntime;
            launchNeedsDesktopPackage = needsDesktopPackage;
            launchStepTotal = (needsCommonRuntime ? 4 : 0) +
                (needsDesktopPackage ? 3 : 0) + 4;
            progress.Value = 0;
            progressText.Text = LauncherLocale.T("共 " + launchStepTotal.ToString(
                CultureInfo.InvariantCulture) + " 步", launchStepTotal.ToString(
                CultureInfo.InvariantCulture) + " steps");
        }

        private void SetStepProgress(int step, int totalSteps, int stepPercent, bool showPercent)
        {
            if (step <= 0 || totalSteps <= 0) return;
            int boundedPercent = Math.Max(0, Math.Min(100, stepPercent));
            int value = (int)Math.Min(100L,
                (((long)step - 1L) * 100L + boundedPercent) / totalSteps);
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = Math.Max(progress.Value, value);
            string text = LauncherLocale.T("第 " + step.ToString(CultureInfo.InvariantCulture) +
                "/" + totalSteps.ToString(CultureInfo.InvariantCulture) + " 步",
                "Step " + step.ToString(CultureInfo.InvariantCulture) + " of " +
                totalSteps.ToString(CultureInfo.InvariantCulture));
            if (showPercent) text += " · " + boundedPercent.ToString(
                CultureInfo.InvariantCulture) + "%";
            progressText.Text = text;
        }

        private async void StartClicked(object sender, EventArgs e)
        {
            if (busy || activeRun != null) return;
            closeRequestedDuringStartup = false;
            if (PortableProcess.IsDesktopRunning(layout))
            {
                closingAfterConfirm = true;
                formIsClosing = true;
                try { ProviderConfiguration.CleanupLegacyAuthentication(layout); } catch { }
                try { PortableScratch.Cleanup(layout); } catch { }
                Close();
                return;
            }
            if (!ArchitectureInfo.HasOfficialDesktopPayload(layout.Architecture))
            {
                MessageBox.Show(LauncherLocale.T("检测到 Windows 架构为 " + layout.ArchitectureName + "。更新当前仅提供 x64 和 arm64 版本。",
                    "This Windows architecture is " + layout.ArchitectureName + ". Updates currently support x64 and arm64 only."),
                    "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!portablePayloadPreflight && !PortableBundle.HasInstallPackages(layout))
            {
                MessageBox.Show(LauncherLocale.T("未找到完整的 LF 发布包。", "The complete LF release package was not found."), "LF Portable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!ProviderConfiguration.HasCompleteApiConfiguration(layout))
            {
                MessageBox.Show(LauncherLocale.T("请先设置 API URL、API Key 和模型。", "Configure the API URL, API key and model before starting."),
                    LauncherLocale.T("需要自定义 API", "Custom API required"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetKeyClicked(this, EventArgs.Empty);
                return;
            }
            bool needsCommonRuntime = !PortableBundle.CommonPayloadComplete(layout);
            bool needsDesktopPackage = !portablePayloadPreflight;
            startWorkflowRunning = true;
            SetBusy(true, null);
            BeginLaunchProgressPlan(needsCommonRuntime, needsDesktopPackage);
            if (needsDesktopPackage || needsCommonRuntime)
            {
                try
                {
                    ReportFirstLaunchPreparationStage(needsCommonRuntime ?
                        FirstLaunchPreparationStage.ValidatingCommonPackage :
                        FirstLaunchPreparationStage.ValidatingDesktopPackage);
                    bool preparedAfterEnsure = await Task.Run(delegate
                    {
                        return PortableBundle.EnsureReady(layout,
                            ReportFirstLaunchPreparationStage, portablePayloadPreflight);
                    });
                    portablePayloadPreflight = preparedAfterEnsure;
                    portablePayloadChecked = preparedAfterEnsure;
                    startupStatePrepared = true;
                }
                catch (Exception ex)
                {
                    SafeLog.TryWrite(layout, "provision", ex);
                    if (CompleteCloseRequestDuringStart()) return;
                    SetBusy(false, null);
                    MessageBox.Show(LauncherLocale.T("首次启动准备失败。错误类型：" + ex.GetType().Name + "。请检查 U 盘空间和连接。",
                        "First-launch preparation failed. Error type: " + ex.GetType().Name + ". Check USB space and connection."), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    RefreshStatus(LauncherLocale.T("首次启动包安装失败", "First-launch package installation failed"));
                    FinishStartWorkflow();
                    return;
                }
            }
            if (CompleteCloseRequestDuringStart()) return;
            try
            {
                // Staging verifies the tree before activation, but the mutation
                // lock is released before this point. Revalidate the installed
                // tree immediately before handing off to the desktop process.
                ReportFirstLaunchPreparationStage(FirstLaunchPreparationStage.VerifyingInstalledDesktop);
                await Task.Run(delegate { EnsurePortablePayloadOnce(); });
                if (CompleteCloseRequestDuringStart()) return;
            }
            catch (Exception ex)
            {
                SafeLog.TryWrite(layout, "portable-branding", ex);
                if (CompleteCloseRequestDuringStart()) return;
                SetBusy(false, null);
                MessageBox.Show(LauncherLocale.T("无法校验 Codex 品牌与完整性。请生成诊断日志。",
                    "Unable to verify Codex branding and integrity. Create a diagnostic report."),
                    "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshStatus(LauncherLocale.T("Codex 完整性校验失败", "Codex integrity verification failed"));
                FinishStartWorkflow();
                return;
            }
            if (!File.Exists(layout.AppExe))
            {
                MessageBox.Show(LauncherLocale.T("应用文件不完整。", "The application files are incomplete."), "LF Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                FinishStartWorkflow();
                return;
            }
            if (!ArchitectureInfo.IsMachineCompatible(layout.OfficialAppExe, layout.Architecture) ||
                !ArchitectureInfo.IsMachineCompatible(layout.AppExe, layout.Architecture))
            {
                MessageBox.Show(LauncherLocale.T("已安装的 Codex Desktop 包与 Windows 架构（" + layout.ArchitectureName + "）不匹配。启动前请更新便携包。",
                    "The installed Codex Desktop payload does not match this Windows architecture (" + layout.ArchitectureName + "). Update the portable payload before starting."),
                    "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                FinishStartWorkflow();
                return;
            }
            if (!File.Exists(layout.CodexExe))
            {
                MessageBox.Show(LauncherLocale.T("应用文件不完整。", "The application files are incomplete."), "LF Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                FinishStartWorkflow();
                return;
            }
            if (!ArchitectureInfo.IsMachineCompatible(layout.CodexExe, layout.Architecture))
            {
                MessageBox.Show(LauncherLocale.T("内置 Codex CLI 与 Windows 架构（" + layout.ArchitectureName + "）不匹配。启动前请更新便携包。",
                    "The bundled Codex CLI does not match this Windows architecture (" + layout.ArchitectureName + "). Update the portable payload before starting."),
                    "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                FinishStartWorkflow();
                return;
            }
            try
            {
                ReportFirstLaunchPreparationStage(FirstLaunchPreparationStage.VerifyingPluginCache);
                int repairedPlugins = await Task.Run(delegate { return EnsureRequiredPluginCache(); });
                if (repairedPlugins > 0)
                    SafeLog.TryWriteEvent(layout, "plugin-cache-repair", "Restored " +
                        repairedPlugins.ToString(CultureInfo.InvariantCulture) + " required plugin(s) before launch.");
                if (CompleteCloseRequestDuringStart()) return;
            }
            catch (Exception ex)
            {
                SafeLog.TryWrite(layout, "plugin-cache-repair", ex);
                if (CompleteCloseRequestDuringStart()) return;
                SetBusy(false, null);
                MessageBox.Show(LauncherLocale.T("必需插件缓存不完整，自动恢复失败。请确认 U 盘连接稳定后重试。\r\n\r\n错误类型：" + ex.GetType().Name,
                    "The required plugin cache is incomplete and recovery failed. Check the USB connection and retry.\r\n\r\nError type: " + ex.GetType().Name),
                    "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                FinishStartWorkflow();
                return;
            }
            // Plugin cache validation above checks only the required manifest and
            // reparse-point boundaries. Avoid a second cache traversal here;
            // executable and marketplace prerequisites are still checked.
            string missingPrerequisite = PortableEnvironment.FindMissingPrerequisite(layout,
                !requiredPluginCacheValidated);
            if (missingPrerequisite != null)
            {
                MessageBox.Show(LauncherLocale.T("便携运行库或插件不完整，禁止启动：\r\n" + missingPrerequisite,
                    "The portable runtime or plugin cache is incomplete; startup is blocked:\r\n" + missingPrerequisite), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                FinishStartWorkflow();
                return;
            }

            string apiKey = null;
            string baseUrl = null;
            string model = null;
            bool preserveHostScratchAfterHandoff = false;
            try
            {
                if (!ProviderConfiguration.TryReadRequiredConfiguration(layout, out baseUrl, out apiKey, out model))
                {
                    MessageBox.Show(LauncherLocale.T("必须先设置有效的 API URL、API Key 和模型，Codex 才能启动。",
                        "Set a valid API URL, API key and model before starting Codex."),
                        LauncherLocale.T("需要自定义 API", "Custom API required"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    FinishStartWorkflow();
                    SetKeyClicked(this, EventArgs.Empty);
                    return;
                }

                if (!startupStatePrepared)
                {
                    layout.EnsureDirectories();
                    ProviderConfiguration.CleanupLegacyAuthentication(layout);
                }
                ReportFirstLaunchPreparationStage(FirstLaunchPreparationStage.RefreshingModelCatalog);
                try
                {
                    int modelCount = await Task.Run(delegate
                    {
                        return ProviderConfiguration.RefreshModelCatalog(layout, baseUrl, apiKey, model);
                    });
                    if (modelCount == 0)
                    {
                        SetBusy(false, null);
                        MessageBox.Show(LauncherLocale.T(
                            "自定义 API 当前没有返回任何可用模型，Codex 无法启动。请检查网关模型配置后重试。",
                            "The custom API currently returns no usable models, so Codex cannot start. Check the gateway model configuration and retry."),
                            LauncherLocale.T("模型列表为空", "Model list is empty"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        FinishStartWorkflow();
                        return;
                    }
                    SafeLog.TryWriteEvent(layout, "model-catalog-refresh", "Loaded " +
                        modelCount.ToString(CultureInfo.InvariantCulture) +
                        " model(s) from the configured gateway.");
                }
                catch (Exception refreshError)
                {
                    // A gateway or pi.dev outage must not make an already
                    // configured portable installation unusable. The last
                    // successful catalog remains on disk and is used by Codex.
                    SafeLog.TryWrite(layout, "model-catalog-refresh", refreshError);
                    List<string> cachedModels = ProviderConfiguration.ReadCatalogModelIds(layout);
                    if (cachedModels.Count == 0)
                    {
                        // Gateway unreachable on a fresh root must not lock the
                        // desktop out.  Fall back to an offline single-model
                        // catalog and let a later start refresh it.
                        ProviderConfiguration.EnsureOfflineFallbackCatalog(layout, model);
                        SafeLog.TryWriteEvent(layout, "model-catalog-offline-fallback",
                            "Gateway unreachable; started with an offline fallback catalog for model " + model);
                    }
                    if (cachedModels.Count != 0)
                    {
                        try
                        {
                            ProviderConfiguration.SelectFirstCatalogModelIfMissing(layout, model,
                                cachedModels);
                        }
                        catch (Exception selectionError)
                        {
                            SafeLog.TryWrite(layout, "model-catalog-selection", selectionError);
                            SetBusy(false, null);
                            MessageBox.Show(LauncherLocale.T(
                                "无法从上一次模型目录恢复有效模型。请检查便携数据盘后重试。",
                                "Unable to restore a valid model from the previous catalog. Check the portable data drive and retry."),
                                LauncherLocale.T("无法恢复模型", "Unable to restore model"),
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            FinishStartWorkflow();
                            return;
                        }
                    }
                    try
                    {
                        ProviderConfiguration.SelectFirstCatalogModelIfMissing(layout, model,
                            cachedModels);
                    }
                    catch (Exception selectionError)
                    {
                        SafeLog.TryWrite(layout, "model-catalog-selection", selectionError);
                        SetBusy(false, null);
                        MessageBox.Show(LauncherLocale.T(
                            "无法从上一次模型目录恢复有效模型。请检查便携数据盘后重试。",
                            "Unable to restore a valid model from the previous catalog. Check the portable data drive and retry."),
                            LauncherLocale.T("无法恢复模型", "Unable to restore model"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        FinishStartWorkflow();
                        return;
                    }
                }
                // First-run package expansion can create profile content after the
                // window's background initialization. Reassert the config-backed
                // permission mode immediately before starting Desktop.
                layout.EnsureConfig();
                layout.EnsureOnboardingSuppressed();
                startupStatePrepared = true;

                // A fixed-disk cache is an optional performance optimization only.
                // When it cannot be created, every cache path falls back to the
                // portable root and startup continues without host prerequisites.
                bool hostScratchEnabled = PortableScratch.TryPrepare(layout);
                List<string> launchArguments = new List<string>();
                launchArguments.Add(IOUtil.QuoteArgument("--user-data-dir=" + layout.ElectronData));
                launchArguments.Add(IOUtil.QuoteArgument("--disk-cache-dir=" + PortableScratch.ActiveChromiumCache(layout)));
                launchArguments.Add(IOUtil.QuoteArgument("--crash-dumps-dir=" + layout.CrashDumps));
                launchArguments.Add(IOUtil.QuoteArgument("--download-default-directory=" + layout.Downloads));
                launchArguments.Add("--no-first-run");
                launchArguments.Add("--no-default-browser-check");
                if (compatibility.Checked)
                {
                    launchArguments.Add("--disable-gpu");
                    launchArguments.Add("--disable-gpu-compositing");
                }
                string arguments = string.Join(" ", launchArguments.ToArray());

                ReportFirstLaunchPreparationStage(FirstLaunchPreparationStage.StartingDesktop);
                Mutex launchMutation = PortableProcess.AcquireMutationMutex(layout, 0);
                if (launchMutation == null)
                    throw new IOException("Another portable start or plugin-cache repair is in progress.");
                try
                {
                    if (PortableProcess.IsDesktopRunning(layout))
                        throw new IOException("Codex Desktop started before handoff; launch was cancelled.");
                    activeRun = StartDesktopProcess(arguments);
                }
                finally
                {
                    PortableProcess.ReleaseMutationMutex(launchMutation);
                }

                SafeLog.TryWriteEvent(layout, "start-attempt",
                    "Codex process tree created. Session cache=" +
                    (hostScratchEnabled ? "local-temporary" : "portable") +
                    "; execution=portable-root; remote control=disabled.");
                JobRun run = activeRun;
                ReportFirstLaunchPreparationStage(FirstLaunchPreparationStage.ConfirmingDesktopStart);
                uint earlyExitCode = 0;
                bool exitedDuringStartup = await Task.Run(delegate
                {
                    return WaitForDesktopStartup(run, out earlyExitCode);
                });
                if (DesktopHandoffWasCancelled(run))
                {
                    if (object.ReferenceEquals(activeRun, run))
                    {
                        activeRun = null;
                        try { run.StopProcessTree(); }
                        finally { try { run.Dispose(); } catch { } }
                    }
                    CompleteCloseRequestDuringStart();
                    return;
                }
                if (exitedDuringStartup)
                {
                    try
                    {
                        await Task.Run(delegate
                        {
                            run.TerminateProcessTreeAndWait(
                                JobRun.ProcessTreeTerminationTimeoutMilliseconds);
                        });
                    }
                    finally
                    {
                        run.Dispose();
                        activeRun = null;
                    }
                    SafeLog.TryWriteEvent(layout, "start-exit",
                        "Codex exited during startup with status 0x" +
                        earlyExitCode.ToString("X8", CultureInfo.InvariantCulture) + ".");
                    throw new DesktopStartupExitException(earlyExitCode);
                }

                if (!run.TryDetachAfterStartup(out earlyExitCode))
                {
                    run.Dispose();
                    activeRun = null;
                    throw new DesktopStartupExitException(earlyExitCode);
                }
                activeRun = null;
                run.Dispose();
                preserveHostScratchAfterHandoff = hostScratchEnabled;
                CleanupLegacyAuthenticationAfterRun();
                SafeLog.TryWriteEvent(layout, "start", "Codex startup confirmation passed.");
                SafeLog.TryWriteEvent(layout, "handoff",
                    "USB-resident Codex process tree detached; launcher exiting.");
                ReportFirstLaunchPreparationStage(FirstLaunchPreparationStage.DesktopStarted);
                closingAfterConfirm = true;
                formIsClosing = true;
                try { Close(); } catch { }
                return;
            }
            catch (DesktopStartupExitException ex)
            {
                JobRun failedRun = activeRun;
                activeRun = null;
                if (failedRun != null)
                {
                    try { failedRun.StopProcessTree(); }
                    finally { try { failedRun.Dispose(); } catch { } }
                }
                CleanupLegacyAuthenticationAfterRun();
                SafeLog.TryWrite(layout, "start", ex);
                if (formIsClosing || IsDisposed || Disposing) return;
                SetBusy(false, null);
                string code = "0x" + ex.ExitCode.ToString("X8", CultureInfo.InvariantCulture);
                MessageBox.Show(LauncherLocale.T(
                    "Codex 在启动确认阶段退出（" + code + "）。若状态为 0xc0000006，请检查 U 盘连接和介质状态后重试；Codex 未启动。",
                    "Codex exited during startup confirmation (" + code + "). For status 0xc0000006, check the USB connection and media before retrying; Codex was not started."),
                    "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshStatus(LauncherLocale.T("启动确认失败", "Startup confirmation failed"));
            }
            catch (Exception ex)
            {
                JobRun failedRun = activeRun;
                activeRun = null;
                if (failedRun != null)
                {
                    try { failedRun.StopProcessTree(); }
                    finally { try { failedRun.Dispose(); } catch (Exception disposeError) { SafeLog.TryWrite(layout, "cleanup", disposeError); } }
                }
                CleanupLegacyAuthenticationAfterRun();
                SafeLog.TryWrite(layout, "start", ex);
                if (formIsClosing || IsDisposed || Disposing) return;
                SetBusy(false, null);
                MessageBox.Show(LauncherLocale.T("启动失败。错误类型：" + ex.GetType().Name + "。请生成诊断日志。", "Startup failed. Error type: " + ex.GetType().Name + ". Create a diagnostic report."), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshStatus(LauncherLocale.T("启动失败", "Startup failed"));
            }
            finally
            {
                if (apiKey != null) apiKey = null;
                if (!preserveHostScratchAfterHandoff) PortableScratch.Cleanup(layout);
                FinishStartWorkflow();
            }
        }

        private void FinishStartWorkflow()
        {
            startWorkflowRunning = false;
            bool shouldClose = closeRequestedDuringStartup;
            if (!shouldClose && !formIsClosing && !IsDisposed && !Disposing) SetBusy(false, null);
            launchNeedsCommonRuntime = false;
            launchNeedsDesktopPackage = false;
            launchStepTotal = 0;
            if (shouldClose && !formIsClosing && !IsDisposed && !Disposing)
                CloseAfterStartupInitialization();
        }

        private bool CompleteCloseRequestDuringStart()
        {
            if (!closeRequestedDuringStartup) return false;
            FinishStartWorkflow();
            return true;
        }

        private bool DesktopHandoffWasCancelled(JobRun run)
        {
            return closeRequestedDuringStartup || formIsClosing || IsDisposed || Disposing ||
                !object.ReferenceEquals(activeRun, run);
        }

        private void ReportFirstLaunchPreparationStage(FirstLaunchProgress progressUpdate)
        {
            if (!InvokeRequired)
            {
                ApplyFirstLaunchPreparationStage(progressUpdate);
                return;
            }
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    ApplyFirstLaunchPreparationStage(progressUpdate);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ReportFirstLaunchPreparationStage(FirstLaunchPreparationStage stage)
        {
            ReportFirstLaunchPreparationStage(new FirstLaunchProgress(stage));
        }

        private void ApplyFirstLaunchPreparationStage(FirstLaunchProgress progressUpdate)
        {
            if (!startWorkflowRunning || formIsClosing || IsDisposed || Disposing) return;
            FirstLaunchPreparationStage stage = progressUpdate.Stage;
            int step = LaunchStepFor(stage);
            if (step <= 0 || launchStepTotal <= 0) return;
            bool measured = (stage == FirstLaunchPreparationStage.ExtractingCommonRuntime ||
                stage == FirstLaunchPreparationStage.ExtractingDesktopPackage) &&
                (progressUpdate.TotalBytes > 0 || progressUpdate.TotalFiles > 0);
            bool completed = stage == FirstLaunchPreparationStage.CommonRuntimeReady ||
                stage == FirstLaunchPreparationStage.DesktopPayloadReady ||
                stage == FirstLaunchPreparationStage.DesktopStarted;
            int stepPercent = completed ? 100 : measured ? MeasuredProgressPercent(progressUpdate) : 0;
            SetStepProgress(step, launchStepTotal, stepPercent, measured);
            switch (stage)
            {
                case FirstLaunchPreparationStage.ValidatingCommonPackage:
                    status.Text = LauncherLocale.T("校验便携运行库", "Validating portable runtime");
                    details.Text = LauncherLocale.T("检查压缩包结构、完整性和可用空间", "Checking package structure, integrity, and free space");
                    break;
                case FirstLaunchPreparationStage.ExtractingCommonRuntime:
                    status.Text = LauncherLocale.T("展开便携运行库", "Extracting portable runtime");
                    details.Text = LauncherLocale.T("解压运行时与离线插件", "Extracting runtime and offline plugins");
                    break;
                case FirstLaunchPreparationStage.VerifyingCommonRuntime:
                    status.Text = LauncherLocale.T("复核便携运行库", "Verifying portable runtime");
                    details.Text = LauncherLocale.T("检查已解压的运行库与插件", "Checking the extracted runtime and plugins");
                    break;
                case FirstLaunchPreparationStage.InstallingCommonRuntime:
                    status.Text = LauncherLocale.T("安装便携运行库", "Installing portable runtime");
                    details.Text = LauncherLocale.T("激活运行库与离线插件", "Activating the runtime and offline plugins");
                    break;
                case FirstLaunchPreparationStage.CommonRuntimeReady:
                    status.Text = LauncherLocale.T("便携运行库已就绪", "Portable runtime ready");
                    details.Text = string.Empty;
                    break;
                case FirstLaunchPreparationStage.ValidatingDesktopPackage:
                    status.Text = LauncherLocale.T("校验 Codex 安装包", "Validating Codex package");
                    details.Text = LauncherLocale.T("检查签名、版本和系统架构", "Checking signature, version, and system architecture");
                    break;
                case FirstLaunchPreparationStage.ExtractingDesktopPackage:
                    status.Text = LauncherLocale.T("展开 Codex", "Extracting Codex");
                    details.Text = LauncherLocale.T("解压桌面程序文件", "Extracting desktop application files");
                    break;
                case FirstLaunchPreparationStage.VerifyingAndBrandingDesktop:
                    status.Text = LauncherLocale.T("校验并应用 LF 品牌", "Verifying and applying LF branding");
                    details.Text = LauncherLocale.T("校验完整性并应用 LF 品牌", "Verifying integrity and applying LF branding");
                    break;
                case FirstLaunchPreparationStage.DesktopPayloadReady:
                    status.Text = LauncherLocale.T("Codex Desktop 已就绪", "Codex Desktop ready");
                    details.Text = string.Empty;
                    break;
                case FirstLaunchPreparationStage.VerifyingInstalledDesktop:
                    status.Text = LauncherLocale.T("复核 Codex", "Verifying Codex");
                    details.Text = LauncherLocale.T("检查 LF 品牌与更新屏蔽", "Checking LF branding and updater blocking");
                    break;
                case FirstLaunchPreparationStage.VerifyingPluginCache:
                    status.Text = LauncherLocale.T("校验插件缓存", "Verifying plugin cache");
                    details.Text = LauncherLocale.T("检查并按需恢复必需插件", "Checking and repairing required plugins if needed");
                    break;
                case FirstLaunchPreparationStage.RefreshingModelCatalog:
                    status.Text = LauncherLocale.T("刷新模型目录", "Refreshing model catalog");
                    details.Text = LauncherLocale.T("从网关获取模型并补充 pi.dev 能力参数", "Fetching gateway models and enriching capabilities from pi.dev");
                    break;
                case FirstLaunchPreparationStage.StartingDesktop:
                    status.Text = LauncherLocale.T("启动 Codex", "Starting Codex");
                    details.Text = LauncherLocale.T("创建便携运行环境", "Creating the portable runtime environment");
                    break;
                case FirstLaunchPreparationStage.ConfirmingDesktopStart:
                    status.Text = LauncherLocale.T("确认 Codex 启动", "Confirming Codex startup");
                    details.Text = LauncherLocale.T("程序就绪后立即交接；慢速介质最多等待 9 秒", "Handing off as soon as the app is ready; slow media may take up to 9 seconds");
                    break;
                case FirstLaunchPreparationStage.DesktopStarted:
                    status.Text = LauncherLocale.T("Codex 已启动", "Codex started");
                    details.Text = string.Empty;
                    break;
            }
            if (measured) details.Text = FormatExtractionProgress(progressUpdate);
            AppendPendingCloseNotice();
        }

        private int LaunchStepFor(FirstLaunchPreparationStage stage)
        {
            int offset = 0;
            if (launchNeedsCommonRuntime)
            {
                switch (stage)
                {
                    case FirstLaunchPreparationStage.ValidatingCommonPackage: return offset + 1;
                    case FirstLaunchPreparationStage.ExtractingCommonRuntime: return offset + 2;
                    case FirstLaunchPreparationStage.VerifyingCommonRuntime: return offset + 3;
                    case FirstLaunchPreparationStage.InstallingCommonRuntime:
                    case FirstLaunchPreparationStage.CommonRuntimeReady: return offset + 4;
                }
                offset += 4;
            }
            if (launchNeedsDesktopPackage)
            {
                switch (stage)
                {
                    case FirstLaunchPreparationStage.ValidatingDesktopPackage: return offset + 1;
                    case FirstLaunchPreparationStage.ExtractingDesktopPackage: return offset + 2;
                    case FirstLaunchPreparationStage.VerifyingAndBrandingDesktop:
                    case FirstLaunchPreparationStage.DesktopPayloadReady: return offset + 3;
                }
                offset += 3;
            }
            switch (stage)
            {
                case FirstLaunchPreparationStage.VerifyingInstalledDesktop: return offset + 1;
                case FirstLaunchPreparationStage.VerifyingPluginCache: return offset + 2;
            }
            offset += 2;
            switch (stage)
            {
                case FirstLaunchPreparationStage.RefreshingModelCatalog: return offset + 1;
            }
            offset += 1;
            switch (stage)
            {
                case FirstLaunchPreparationStage.StartingDesktop:
                case FirstLaunchPreparationStage.ConfirmingDesktopStart:
                case FirstLaunchPreparationStage.DesktopStarted: return offset + 1;
                default: return 0;
            }
        }

        private static int MeasuredProgressPercent(FirstLaunchProgress update)
        {
            return MeasuredProgressPercent(update.CompletedBytes, update.TotalBytes,
                update.CompletedFiles, update.TotalFiles);
        }

        private static int MeasuredProgressPercent(long completedBytes, long totalBytes,
            int completedFiles, int totalFiles)
        {
            int percent = 0;
            if (totalBytes > 0)
                percent = (int)Math.Min(100L, Math.Max(0L,
                    completedBytes * 100L / totalBytes));
            else if (totalFiles > 0)
                percent = Math.Min(100, Math.Max(0,
                    completedFiles * 100 / totalFiles));
            bool complete = (totalFiles <= 0 || completedFiles >= totalFiles) &&
                (totalBytes <= 0 || completedBytes >= totalBytes);
            if (!complete && percent >= 100) percent = 99;
            return complete ? 100 : percent;
        }

        private static string FormatExtractionProgress(FirstLaunchProgress update)
        {
            return FormatTransferProgress(update.CompletedBytes, update.TotalBytes,
                update.CompletedFiles, update.TotalFiles);
        }

        private static string FormatTransferProgress(long completedBytes, long totalBytes,
            int completedFiles, int totalFiles)
        {
            string fileProgress = completedFiles.ToString("N0", CultureInfo.CurrentCulture) +
                " / " + totalFiles.ToString("N0", CultureInfo.CurrentCulture) +
                LauncherLocale.T(" 个文件", " files");
            if (totalBytes <= 0) return fileProgress;
            return fileProgress + " · " + FormatByteCount(completedBytes) + " / " +
                FormatByteCount(totalBytes);
        }

        private static string FormatByteCount(long value)
        {
            double amount = Math.Max(0L, value);
            string unit = "B";
            if (amount >= 1024.0)
            {
                amount /= 1024.0;
                unit = "KB";
            }
            if (amount >= 1024.0)
            {
                amount /= 1024.0;
                unit = "MB";
            }
            if (amount >= 1024.0)
            {
                amount /= 1024.0;
                unit = "GB";
            }
            return amount.ToString(amount >= 100.0 ? "0" : amount >= 10.0 ? "0.0" : "0.00",
                CultureInfo.CurrentCulture) + " " + unit;
        }

        private int EnsureRequiredPluginCache()
        {
            // Cache the lightweight manifest result for this open launcher
            // window so the immediately following prerequisite check does not
            // repeat the same directory checks.
            if (ProviderConfiguration.RequiredPluginCacheComplete(layout))
            {
                requiredPluginCacheValidated = true;
                return 0;
            }
            int repaired = ProviderConfiguration.EnsureRequiredPluginCache(layout);
            if (!ProviderConfiguration.RequiredPluginCacheComplete(layout))
                throw new InvalidDataException("Required plugin cache is still incomplete after recovery.");
            requiredPluginCacheValidated = true;
            return repaired;
        }

        private void EnsurePortablePayloadOnce()
        {
            if (portablePayloadChecked) return;
            PortableBranding.EnsurePortablePayload(layout);
            portablePayloadChecked = true;
        }

        private void InvalidatePayloadPreflight()
        {
            portablePayloadChecked = false;
            portablePayloadPreflight = false;
            requiredPluginCacheValidated = false;
        }

        private static bool WaitForDesktopStartup(JobRun run, out uint exitCode)
        {
            exitCode = 0;
            // A normal Electron desktop creates its message queue well before
            // the first window is painted.  Use that signal to avoid an
            // unconditional multi-second handoff delay, while retaining the
            // original full timeout for slow USB media or non-GUI failures.
            bool inputIdle = false;
            try
            {
                inputIdle = run.TryWaitForInputIdle(DesktopStartupIdleProbeMilliseconds);
            }
            catch
            {
                // A handle/API failure must not turn a successful desktop
                // launch into a launcher error; use the legacy wait path.
                inputIdle = false;
            }
            if (inputIdle)
                return run.TryGetEarlyExit(DesktopStartupIdleGraceMilliseconds, out exitCode);
            int remaining = DesktopStartupConfirmationMilliseconds -
                DesktopStartupIdleProbeMilliseconds;
            if (remaining < 0) remaining = 0;
            return run.TryGetEarlyExit(remaining, out exitCode);
        }

        private JobRun StartDesktopProcess(string arguments)
        {
            string baseUrl;
            string apiKey;
            string model;
            if (!ProviderConfiguration.TryReadRequiredConfiguration(layout, out baseUrl, out apiKey, out model))
                throw new InvalidDataException("Portable API configuration disappeared before desktop startup.");
            Dictionary<string, string> environment = PortableEnvironment.Build(layout, apiKey);
            try
            {
                string debugPort = Environment.GetEnvironmentVariable("LF_DEBUG_CDP_PORT");
                int debugPortValue;
                if (!string.IsNullOrEmpty(debugPort) && int.TryParse(debugPort,
                        NumberStyles.None, CultureInfo.InvariantCulture, out debugPortValue) &&
                    debugPortValue > 0 && debugPortValue <= 65535)
                {
                    // Diagnostic hook: expose the Electron renderer over the
                    // Chrome DevTools Protocol for UI automation (read DOM,
                    // screenshot, click) even when the window is not visible
                    // to this session.  Off by default; used only for local
                    // verification and never in published defaults.
                    arguments = arguments + " --remote-debugging-port=" +
                        debugPortValue.ToString(CultureInfo.InvariantCulture) +
                        " --remote-allow-origins=*";
                }
                return JobRun.Start(layout.AppExe, arguments, layout.CurrentApp, environment,
                    layout.RootToken);
            }
            finally
            {
                environment.Remove(ProviderConfiguration.ApiKeyEnvironmentVariable);
                apiKey = null;
                baseUrl = null;
                model = null;
            }
        }

        private void SetKeyClicked(object sender, EventArgs e)
        {
            if (busy) return;
            KeySetupResult result = KeySetupDialog.Ask(this,
                ProviderConfiguration.ReadEffectiveBaseUrl(layout),
                ProviderConfiguration.ReadEffectiveModel(layout),
                ProviderConfiguration.ReadStoredApiKey(layout),
                ProviderConfiguration.ReadCatalogModelIds(layout));
            if (result == null) return;
            try
            {
                SetBusy(true, LauncherLocale.T("正在保存自定义 API…", "Saving custom API…"));
                layout.EnsureDirectories();
                ProviderConfiguration.Save(layout, result.BaseUrl, result.Model, result.ApiKey);
                SafeLog.TryWriteEvent(layout, "custom-api-set", "Custom API URL, key and model saved in portable data.");
                MessageBox.Show(LauncherLocale.T("自定义 API 已保存。", "Custom API saved."), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SafeLog.TryWrite(layout, "key-set", ex);
                MessageBox.Show(LauncherLocale.T("无法保存自定义 API。错误类型：" + ex.GetType().Name, "Unable to save custom API. Error type: " + ex.GetType().Name), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                result.Clear();
                SetBusy(false, null);
                RefreshStatus(LauncherLocale.T("就绪", "Ready"));
            }
        }

        private void ClearKeyClicked(object sender, EventArgs e)
        {
            if (busy) return;
            if (MessageBox.Show(LauncherLocale.T("将清除 API URL、API Key 和模型；清除后 Codex 禁止启动。是否继续？", "This clears the API URL, API key and model; Codex cannot start until they are configured again. Continue?"), LauncherLocale.T("清除 API 配置", "Clear API configuration"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                IOUtil.DeleteFileIfExists(layout.VaultFile);
                IOUtil.DeleteFileIfExists(layout.PlainKeyFile);
                IOUtil.DeleteFileIfExists(layout.BaseUrlFile);
                IOUtil.DeleteFileIfExists(layout.ModelFile);
                IOUtil.DeleteFileIfExists(layout.ModelCatalogFile);
                ProviderConfiguration.CleanupLegacyAuthentication(layout);
                if (Directory.Exists(layout.CrashDumps)) IOUtil.DeleteDirectoryWithin(layout.CrashDumps, layout.Logs);
                Directory.CreateDirectory(layout.CrashDumps);
                layout.EnsureConfig();
                SafeLog.TryWriteEvent(layout, "custom-api-clear", "Custom API settings cleared.");
                RefreshStatus(LauncherLocale.T("自定义 API 已清除", "Custom API cleared"));
            }
            catch (Exception ex)
            {
                SafeLog.TryWrite(layout, "key-clear", ex);
                MessageBox.Show(LauncherLocale.T("清除失败。错误类型：" + ex.GetType().Name, "Clear failed. Error type: " + ex.GetType().Name), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DiagnosticsClicked(object sender, EventArgs e)
        {
            try
            {
                string path = Diagnostics.Create(layout);
                MessageBox.Show(LauncherLocale.T("诊断已保存：\r\n" + path, "Diagnostics saved:\r\n" + path), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(LauncherLocale.T("无法生成诊断。错误类型：" + ex.GetType().Name, "Unable to create diagnostics. Error type: " + ex.GetType().Name), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenDataClicked(object sender, EventArgs e)
        {
            try
            {
                layout.EnsureDirectories();
                Process.Start(new ProcessStartInfo("explorer.exe", "/e," + IOUtil.QuoteArgument(layout.DataRoot)) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SafeLog.TryWrite(layout, "open-data", ex);
                MessageBox.Show(LauncherLocale.T("无法打开资料目录。", "Unable to open the data folder."), "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void FormIsClosing(object sender, FormClosingEventArgs e)
        {
            if (closingAfterConfirm) return;
            if (activeRun == null && ((startupTask != null && !startupTask.IsCompleted) ||
                startWorkflowRunning))
            {
                // Do not terminate the process in the middle of an atomic config or
                // release extraction/transactional plugin repair. Keep the UI alive
                // until the current worker operation completes, then close safely.
                closeRequestedDuringStartup = true;
                e.Cancel = true;
                AppendPendingCloseNotice();
                return;
            }
            if (activeRun == null)
            {
                formIsClosing = true;
                try { ProviderConfiguration.CleanupLegacyAuthentication(layout); } catch { }
                PortableScratch.Cleanup(layout);
                return;
            }
            DialogResult answer = MessageBox.Show(LauncherLocale.T("关闭启动器会同时结束由它启动的 Codex 进程。是否继续？", "Closing the launcher will also stop the Codex process it started. Continue?"),
                LauncherLocale.T("Codex 正在运行", "Codex is running"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            closingAfterConfirm = true;
            formIsClosing = true;
            try
            {
                JobRun run = activeRun;
                activeRun = null;
                run.StopProcessTree();
                run.Dispose();
                CleanupLegacyAuthenticationAfterRun();
                PortableScratch.Cleanup(layout);
            }
            catch { }
        }

        private void CleanupLegacyAuthenticationAfterRun()
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    ProviderConfiguration.CleanupLegacyAuthentication(layout);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 19) Thread.Sleep(100);
                }
            }
            if (lastError != null) SafeLog.TryWrite(layout, "cleanup", lastError);
        }
    }
}

namespace CodexPortable
{
    internal enum TokenElevationState
    {
        Unavailable = -1,
        Standard = 0,
        Elevated = 1
    }

    internal static class WindowsTokenElevation
    {
        private const uint TokenQuery = 0x0008;
        private const int TokenElevationInformationClass = 20;

        internal static TokenElevationState Query(out int nativeError)
        {
            nativeError = 0;
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TokenQuery, out token))
                {
                    nativeError = Marshal.GetLastWin32Error();
                    return TokenElevationState.Unavailable;
                }

                NativeMethods.TOKEN_ELEVATION elevation;
                uint returnedLength;
                uint expectedLength = (uint)Marshal.SizeOf(typeof(NativeMethods.TOKEN_ELEVATION));
                if (!NativeMethods.GetTokenInformation(token, TokenElevationInformationClass, out elevation,
                    expectedLength, out returnedLength))
                {
                    nativeError = Marshal.GetLastWin32Error();
                    return TokenElevationState.Unavailable;
                }
                if (returnedLength < expectedLength)
                {
                    nativeError = 13; // ERROR_INVALID_DATA
                    return TokenElevationState.Unavailable;
                }
                return elevation.TokenIsElevated == 0 ? TokenElevationState.Standard : TokenElevationState.Elevated;
            }
            catch (DllNotFoundException ex)
            {
                nativeError = ex.HResult;
                return TokenElevationState.Unavailable;
            }
            catch (EntryPointNotFoundException ex)
            {
                nativeError = ex.HResult;
                return TokenElevationState.Unavailable;
            }
            catch (BadImageFormatException ex)
            {
                nativeError = ex.HResult;
                return TokenElevationState.Unavailable;
            }
            finally
            {
                if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
            }
        }
    }

    internal static class Diagnostics
    {
        internal static string Create(PortableLayout layout)
        {
            layout.EnsureDirectories();
            StringBuilder text = new StringBuilder();
            text.AppendLine("Codex Portable diagnostics");
            text.AppendLine("GeneratedUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            text.AppendLine("LauncherVersion=" + Assembly.GetExecutingAssembly().GetName().Version.ToString());
            text.AppendLine("OperatingSystem=" + Environment.OSVersion.VersionString);
            text.AppendLine("WindowsArchitecture=" + layout.ArchitectureName);
            text.AppendLine("OfficialDesktopPayloadAvailable=" + ArchitectureInfo.HasOfficialDesktopPayload(layout.Architecture).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("Is64BitOS=" + Environment.Is64BitOperatingSystem.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("Is64BitProcess=" + Environment.Is64BitProcess.ToString(CultureInfo.InvariantCulture));
            int elevationError;
            TokenElevationState elevation = WindowsTokenElevation.Query(out elevationError);
            text.AppendLine("ProcessTokenElevation=" + elevation.ToString());
            if (elevation == TokenElevationState.Unavailable)
                text.AppendLine("ProcessTokenElevationError=" + elevationError.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("ClrVersion=" + Environment.Version.ToString());
            text.AppendLine("Root=" + layout.Root);
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(layout.Root));
                text.AppendLine("DriveType=" + drive.DriveType.ToString());
                text.AppendLine("DriveFormat=" + drive.DriveFormat);
                text.AppendLine("DriveFreeBytes=" + drive.AvailableFreeSpace.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { text.AppendLine("DriveInfoError=" + ex.GetType().Name); }

            AppendFile(text, "CodexDesktop", layout.AppExe, false);
            AppendFile(text, "OfficialCodexPayload", layout.OfficialAppExe, false);
            text.AppendLine("DesktopPayloadTrust=Signed MSIX plus pinned identity verified before extraction");
            text.AppendLine("PortableDesktopProcessName=" + PortableBranding.DesktopExecutableName);
            text.AppendLine("PortableBrandingPrepared=" + PortableBranding.IsPrepared(layout).ToString(CultureInfo.InvariantCulture));
            AppendFile(text, "CodexCli", layout.CodexExe, false);
            text.AppendLine("RuntimeDirectory=" + Directory.Exists(layout.Runtime).ToString(CultureInfo.InvariantCulture));
            AppendFile(text, "RuntimeNode", Path.Combine(layout.Runtime, "dependencies", "node", "bin", "node.exe"), false);
            AppendFile(text, "RuntimePython", Path.Combine(layout.Runtime, "dependencies", "python", "python.exe"), false);
            AppendFile(text, "RuntimeGit", Path.Combine(layout.Runtime, "dependencies", "native", "git", "cmd", "git.exe"), false);
            AppendFile(text, "PortableDotnet", Path.Combine(layout.Tools, "dotnet", "dotnet.exe"), false);
            AppendFile(text, "PortableGh", File.Exists(Path.Combine(layout.Tools, "gh", "bin", "gh.exe")) ?
                Path.Combine(layout.Tools, "gh", "bin", "gh.exe") : Path.Combine(layout.Tools, "gh", "gh.exe"), false);
            text.AppendLine("ConfigExists=" + File.Exists(layout.ConfigFile).ToString(CultureInfo.InvariantCulture));
            if (File.Exists(layout.ConfigFile))
            {
                string config = File.ReadAllText(layout.ConfigFile, Encoding.UTF8);
                text.AppendLine("ConfigCustomProvider=" + config.Contains("model_provider = \"portable_custom\"").ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfigChatGptBackendBlocked=" + config.Contains("chatgpt_base_url = \"http://127.0.0.1:9\"").ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfigNoOpenAiAuth=" + config.Contains("requires_openai_auth = false").ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfigResponsesWireApi=" + config.Contains("wire_api = \"responses\"").ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfigReasoningEffortMax=" + config.Contains(ProviderConfiguration.ReasoningEffortConfigLine).ToString(CultureInfo.InvariantCulture));
                string configuredApprovalPolicy;
                string configuredSandboxMode;
                bool permissionsValid = ProviderConfiguration.TryReadPermissionSettings(config,
                    out configuredApprovalPolicy, out configuredSandboxMode);
                text.AppendLine("ConfigPermissionsValid=" + permissionsValid.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfigApprovalPolicy=" + (configuredApprovalPolicy ?? "<invalid>"));
                text.AppendLine("ConfigSandboxMode=" + (configuredSandboxMode ?? "<invalid>"));
                string configuredFollowUpQueueMode;
                bool followUpQueueModeValid = ProviderConfiguration.TryReadFollowUpQueueMode(config,
                    out configuredFollowUpQueueMode);
                text.AppendLine("ConfigFollowUpQueueModeValid=" +
                    followUpQueueModeValid.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfigFollowUpQueueMode=" + (configuredFollowUpQueueMode ?? "<invalid>"));
                // Retain these booleans as quick checks for the default profile,
                // while the effective values above reflect config.toml authority.
                text.AppendLine("ConfigDangerFullAccess=" +
                    (permissionsValid && string.Equals(configuredSandboxMode, ProviderConfiguration.DefaultSandboxMode,
                        StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfigApprovalNever=" +
                    (permissionsValid && string.Equals(configuredApprovalPolicy, ProviderConfiguration.DefaultApprovalPolicy,
                        StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture));
                int analyticsSection = config.IndexOf("[analytics]", StringComparison.OrdinalIgnoreCase);
                text.AppendLine("ConfigAnalyticsDisabled=" + (analyticsSection >= 0 &&
                    config.IndexOf("enabled = false", analyticsSection, StringComparison.OrdinalIgnoreCase) >= 0).ToString(CultureInfo.InvariantCulture));
                text.AppendLine("ConfiguredPluginCount=" + ProviderConfiguration.CountConfiguredPlugins(
                    config, layout).ToString(CultureInfo.InvariantCulture));
            }
            text.AppendLine("DefaultApprovalPolicy=" + ProviderConfiguration.DefaultApprovalPolicy);
            text.AppendLine("DefaultSandboxMode=" + ProviderConfiguration.DefaultSandboxMode);
            text.AppendLine("DefaultReasoningEffort=" + ProviderConfiguration.DefaultReasoningEffort);
            text.AppendLine("DefaultFollowUpQueueMode=" + ProviderConfiguration.DefaultFollowUpQueueMode);
            text.AppendLine("DesktopOnboardingSuppressed=" + PortableOnboarding.IsSuppressed(layout).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("DesktopAppBrand=" + PortableEnvironment.DesktopBrand);
            text.AppendLine("DesktopAppUserModelId=" + PortableBranding.AppUserModelId);
            text.AppendLine("DesktopUpdaterPolicy=Disabled; replace the LF package manually");
            Dictionary<string, string> diagnosticEnvironment = PortableEnvironment.Build(layout, null);
            string remoteControlDisabled;
            text.AppendLine("RemoteControlDisabled=" +
                (diagnosticEnvironment.TryGetValue(PortableEnvironment.RemoteControlDisabledEnvironmentVariable, out remoteControlDisabled) &&
                string.Equals(remoteControlDisabled, "1", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture));
            diagnosticEnvironment.Clear();
            text.AppendLine("PerformanceScratchPolicy=host-temp-per-session; cleanup-on-launch-failure; stale-reclaim-on-later-launch; portable fallback on failure");
            text.AppendLine("PlaintextApiKeyConfigured=" + (!string.IsNullOrEmpty(ProviderConfiguration.ReadStoredApiKey(layout))).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("CustomBaseUrlConfigured=" + (!string.IsNullOrEmpty(ProviderConfiguration.ReadEffectiveBaseUrl(layout))).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("CustomModelConfigured=" + (!string.IsNullOrEmpty(ProviderConfiguration.ReadEffectiveModel(layout))).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("AuthJsonAbsent=" + (!File.Exists(layout.AuthFile)).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("LegacyVaultAbsent=" + (!File.Exists(layout.VaultFile)).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("RequiredPluginCacheComplete=" + ProviderConfiguration.RequiredPluginCacheComplete(layout).ToString(CultureInfo.InvariantCulture));
            text.AppendLine("SignaturePolicy=WinVerifyTrust plus pinned MSIX identity/publisher/architecture manifest");
            text.AppendLine("RedirectedVariableNames=CODEX_APP_BRAND,CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED,CODEX_ELECTRON_USER_DATA_PATH,CODEX_HOME,CODEX_SQLITE_HOME,CODEX_PORTABLE_ROOT,CODEX_PORTABLE_API_KEY,HOME,USERPROFILE,APPDATA,LOCALAPPDATA,LOCALAPPDATALOW,TEMP,TMP,TMPDIR,XDG_CONFIG_HOME,XDG_CACHE_HOME,XDG_DATA_HOME,XDG_STATE_HOME,DOTNET_CLI_HOME,DOTNET_BUNDLE_EXTRACT_BASE_DIR,DOTNET_ROOT,GH_CONFIG_DIR,NPM_CONFIG_CACHE,PIP_CACHE_DIR,UV_CACHE_DIR");
            text.AppendLine("ChromiumPaths=electron-user-data-portable,session-cache-host-temp,logs-crash-dumps-portable,data-downloads-portable");
            text.AppendLine("SecretValuesIncluded=false");

            string file = Path.Combine(layout.Logs, "diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            IOUtil.AtomicWriteText(file, text.ToString());
            return file;
        }

        private static void AppendFile(StringBuilder text, string name, string path, bool signature)
        {
            bool exists = File.Exists(path);
            text.AppendLine(name + "Exists=" + exists.ToString(CultureInfo.InvariantCulture));
            if (!exists) return;
            try
            {
                FileInfo info = new FileInfo(path);
                text.AppendLine(name + "Bytes=" + info.Length.ToString(CultureInfo.InvariantCulture));
                string version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                text.AppendLine(name + "FileVersion=" + (version ?? ""));
                if (signature) text.AppendLine(name + "TrustedSignature=" + SignatureVerifier.Verify(path).ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { text.AppendLine(name + "InfoError=" + ex.GetType().Name); }
        }
    }

    internal static class SafeLog
    {
        internal static void TryWrite(PortableLayout layout, string operation, Exception error)
        {
            TryWriteEvent(layout, operation, "Failure type=" + error.GetType().Name + ", hresult=" +
                error.HResult.ToString("X8", CultureInfo.InvariantCulture) + ", message=" + error.Message + ".");
        }

        internal static void TryWriteEvent(PortableLayout layout, string operation, string message)
        {
            try
            {
                Directory.CreateDirectory(layout.Logs);
                string file = Path.Combine(layout.Logs, "launcher-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                string safeOperation = Sanitize(operation);
                string safeMessage = Sanitize(message);
                string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " [" + safeOperation + "] " + safeMessage + Environment.NewLine;
                File.AppendAllText(file, line, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string Sanitize(string value)
        {
            if (value == null) return "";
            string clean = value.Replace("\r", " ").Replace("\n", " ");
            return clean.Length <= 500 ? clean : clean.Substring(0, 500);
        }
    }

    internal static class PortableScratch
    {
        private const string ScratchProductDirectory = "LFPortable";
        private const string ScratchDirectory = "scratch";
        private const string SessionPrefix = "session-";

        internal static bool TryPrepare(PortableLayout layout)
        {
            try
            {
                string baseRoot = GetValidatedBaseRoot(layout, true);
                string current = GetVerifiedSessionRoot(layout, baseRoot, true);
                EnsureConfiguredSessionDirectory(layout.HostTemp, current, "temp", true);
                EnsureConfiguredSessionDirectory(layout.HostXdgCache, current, "xdg-cache", true);
                EnsureConfiguredSessionDirectory(layout.HostChromiumCache, current, "chromium-cache", true);
                EnsureConfiguredSessionDirectory(layout.HostDotnetBundle, current, "dotnet-bundle", true);
                EnsureConfiguredSessionDirectory(layout.HostNpmCache, current, "npm-cache", true);
                EnsureConfiguredSessionDirectory(layout.HostPipCache, current, "pip-cache", true);
                EnsureConfiguredSessionDirectory(layout.HostUvCache, current, "uv-cache", true);
                // Stale session cleanup is maintenance work.  Creating the current
                // scratch tree is the only launch-critical operation, so defer the
                // potentially expensive recursive deletes until after startup.
                Task.Run(delegate { CleanupStaleSessions(layout, baseRoot, current); });
                return true;
            }
            catch (Exception ex)
            {
                SafeLog.TryWrite(layout, "performance-cache", ex);
                Cleanup(layout);
                return false;
            }
        }

        private static void CleanupStaleSessions(PortableLayout layout, string baseRoot, string current)
        {
            try
            {
                EnsureFixedDirectoryChain(baseRoot, false);
                string[] stale = Directory.GetDirectories(baseRoot, "session-*");
                DateTime cutoff = DateTime.UtcNow.AddDays(-2);
                for (int i = 0; i < stale.Length; i++)
                {
                    string candidate = NormalizeDirectoryPath(stale[i]);
                    if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        ValidateSessionRoot(baseRoot, candidate, false);
                        if (Directory.GetLastWriteTimeUtc(candidate) < cutoff)
                        {
                            ValidateSessionRoot(baseRoot, candidate, false);
                            IOUtil.DeleteDirectoryWithin(candidate, baseRoot);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                SafeLog.TryWrite(layout, "performance-cache-cleanup", ex);
            }
        }

        internal static bool IsPrepared(PortableLayout layout)
        {
            try
            {
                string baseRoot = GetValidatedBaseRoot(layout, false);
                string sessionRoot = GetVerifiedSessionRoot(layout, baseRoot, false);
                EnsureConfiguredSessionDirectory(layout.HostTemp, sessionRoot, "temp", false);
                EnsureConfiguredSessionDirectory(layout.HostXdgCache, sessionRoot, "xdg-cache", false);
                EnsureConfiguredSessionDirectory(layout.HostChromiumCache, sessionRoot, "chromium-cache", false);
                EnsureConfiguredSessionDirectory(layout.HostDotnetBundle, sessionRoot, "dotnet-bundle", false);
                EnsureConfiguredSessionDirectory(layout.HostNpmCache, sessionRoot, "npm-cache", false);
                EnsureConfiguredSessionDirectory(layout.HostPipCache, sessionRoot, "pip-cache", false);
                EnsureConfiguredSessionDirectory(layout.HostUvCache, sessionRoot, "uv-cache", false);
                return true;
            }
            catch { return false; }
        }

        internal static string ActiveChromiumCache(PortableLayout layout)
        {
            return IsPrepared(layout) ? layout.HostChromiumCache : layout.ChromiumCache;
        }

        internal static void Cleanup(PortableLayout layout)
        {
            try
            {
                string baseRoot = GetValidatedBaseRoot(layout, false);
                string sessionRoot = GetVerifiedSessionRoot(layout, baseRoot, false);
                IOUtil.DeleteDirectoryWithin(sessionRoot, baseRoot);
            }
            catch { }
        }

        private static string GetVerifiedSessionRoot(PortableLayout layout, string baseRoot,
            bool create)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            return ValidateSessionRoot(baseRoot, layout.HostScratchRoot, create);
        }

        private static string GetValidatedBaseRoot(PortableLayout layout, bool create)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            string hostLocalAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(hostLocalAppData))
                throw new InvalidOperationException("The host local application-data directory is unavailable.");
            string expectedBase = NormalizeDirectoryPath(Path.Combine(hostLocalAppData,
                ScratchProductDirectory, ScratchDirectory));
            string scratch = NormalizeDirectoryPath(layout.HostScratchRoot);
            string configuredBase = NormalizeDirectoryPath(Path.GetDirectoryName(scratch));
            string portableRoot = NormalizeDirectoryPath(layout.Root);
            if (!string.Equals(expectedBase, configuredBase, StringComparison.OrdinalIgnoreCase) ||
                IsSameOrDescendant(scratch, portableRoot))
                throw new InvalidOperationException("Invalid host scratch path.");
            EnsureFixedDirectoryChain(expectedBase, create);
            return expectedBase;
        }

        private static string ValidateSessionRoot(string baseRoot, string sessionPath, bool create)
        {
            string normalizedBase = NormalizeDirectoryPath(baseRoot);
            string session = NormalizeDirectoryPath(sessionPath);
            string sessionParent = Path.GetDirectoryName(session);
            string sessionName = Path.GetFileName(session);
            if (!string.Equals(NormalizeDirectoryPath(sessionParent), normalizedBase,
                    StringComparison.OrdinalIgnoreCase) ||
                !sessionName.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase) ||
                sessionName.Length <= SessionPrefix.Length)
                throw new InvalidOperationException("The host scratch session path is unsafe.");
            EnsureFixedDirectoryChain(session, create);
            return session;
        }

        private static void EnsureConfiguredSessionDirectory(string configuredPath,
            string sessionRoot, string name, bool create)
        {
            string expected = NormalizeDirectoryPath(Path.Combine(sessionRoot, name));
            string configured = NormalizeDirectoryPath(configuredPath);
            if (!string.Equals(expected, configured, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The host scratch child path is unsafe.");
            EnsureFixedDirectoryChain(expected, create);
        }

        private static void EnsureFixedDirectoryChain(string path, bool create)
        {
            string target = NormalizeDirectoryPath(path);
            string volumeRoot = Path.GetPathRoot(target);
            if (string.IsNullOrEmpty(volumeRoot))
                throw new InvalidOperationException("The host scratch volume is unavailable.");
            DriveInfo drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                throw new InvalidOperationException("The host scratch directory must be on a fixed local volume.");

            string root = NormalizeDirectoryPath(volumeRoot);
            EnsureRegularFixedDirectory(root, false);
            if (!string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            {
                string relative = target.Substring(root.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string[] segments = relative.Split(new char[] {
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
                }, StringSplitOptions.RemoveEmptyEntries);
                string current = root;
                for (int i = 0; i < segments.Length; i++)
                {
                    current = Path.Combine(current, segments[i]);
                    EnsureRegularFixedDirectory(current, create);
                }
            }
            // Directory.CreateDirectory can traverse more than one level. Recheck
            // the whole chain after a creating pass before it is used for scratch data.
            if (create) EnsureFixedDirectoryChain(target, false);
        }

        private static void EnsureRegularFixedDirectory(string path, bool create)
        {
            uint attributes = NativeMethods.GetFileAttributes(path);
            if (attributes == NativeMethods.InvalidFileAttributes)
            {
                int error = Marshal.GetLastWin32Error();
                if (!create || (error != 2 && error != 3))
                    throw new Win32Exception(error, "Unable to verify the host scratch directory.");
                Directory.CreateDirectory(path);
                attributes = NativeMethods.GetFileAttributes(path);
                if (attributes == NativeMethods.InvalidFileAttributes)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Unable to create the host scratch directory.");
            }
            FileAttributes directoryAttributes = (FileAttributes)attributes;
            if ((directoryAttributes & FileAttributes.Directory) == 0 ||
                (directoryAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Host scratch directories cannot be reparse points.");
        }

        private static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Directory path is missing.", "path");
            string full = Path.GetFullPath(path);
            string volumeRoot = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(volumeRoot)) throw new InvalidOperationException("Directory path has no volume root.");
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) return volumeRoot;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsSameOrDescendant(string candidate, string root)
        {
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class IOUtil
    {
        internal static void EnsureDirectoryWithinNoReparse(string path, string allowedRoot)
        {
            string root = NormalizeDirectoryPath(allowedRoot);
            string target = NormalizeDirectoryPath(path);
            // NormalizeDirectoryPath keeps the trailing separator for a volume
            // root (for example, E:\).  Do not append a second separator when
            // constructing the containment prefix; USB packages commonly live
            // directly at the drive root.
            string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal) ||
                root.EndsWith(Path.AltDirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal) ? root : root + Path.DirectorySeparatorChar;
            if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith(rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Directory is outside the portable root: " + target);

            EnsureRegularDirectory(root, false);
            if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
            string relative = target.Substring(root.Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = root;
            string[] segments = relative.Split(new char[] {
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
            }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                EnsureRegularDirectory(current, true);
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string full = Path.GetFullPath(path);
            string volumeRoot = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(volumeRoot) && string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) return volumeRoot;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void EnsureRegularDirectory(string path, bool create)
        {
            if (File.Exists(path) && !Directory.Exists(path))
                throw new IOException("Portable directory path is a file: " + path);
            if (!Directory.Exists(path))
            {
                if (!create)
                    throw new DirectoryNotFoundException("Portable root is missing: " + path);
                Directory.CreateDirectory(path);
            }
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Portable directories cannot be reparse points: " + path);
        }

        internal static void AtomicWriteText(string path, string text)
        {
            AtomicWriteBytes(path, new UTF8Encoding(false).GetBytes(text));
        }

        internal static void AtomicWriteSensitiveText(string path, string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            try { AtomicWriteBytes(path, bytes); }
            finally { CryptoUtil.Zero(bytes); }
        }

        internal static void AtomicWriteBytes(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            try
            {
                AtomicReplaceFile(temporary, path);
            }
            finally { TryDelete(temporary); }
        }

        internal static void AtomicReplaceFile(string temporary, string path)
        {
            if (!File.Exists(temporary))
                throw new FileNotFoundException("Atomic replacement source is missing.", temporary);
            string directory = Path.GetDirectoryName(path);
            string temporaryDirectory = Path.GetDirectoryName(temporary);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(temporaryDirectory) ||
                !string.Equals(Path.GetFullPath(directory).TrimEnd('\\'),
                    Path.GetFullPath(temporaryDirectory).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Atomic replacement files must share one directory.");
            Directory.CreateDirectory(directory);
            if (!File.Exists(path))
            {
                File.Move(temporary, path);
                return;
            }
            try
            {
                File.Replace(temporary, path, null, true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }

            string old = Path.Combine(directory, ".old-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Move(path, old);
            try
            {
                File.Move(temporary, path);
                TryDelete(old);
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(old)) File.Move(old, path);
                throw;
            }
        }

        internal static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch { }
        }

        internal static void DeleteFileIfExists(string path)
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            if (File.Exists(path)) throw new IOException("File deletion did not complete.");
        }

        internal static void DeleteDirectoryWithin(string target, string allowedRoot)
        {
            string targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootFull = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (targetFull.Length <= rootFull.Length || !targetFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing unsafe directory deletion.");
            if (Directory.Exists(targetFull))
            {
                string extended = targetFull.StartsWith("\\\\", StringComparison.Ordinal) ?
                    "\\\\?\\UNC\\" + targetFull.Substring(2) : "\\\\?\\" + targetFull;
                DeleteDirectoryLongPath(extended);
            }
        }

        private static void DeleteDirectoryLongPath(string extendedDirectory)
        {
            NativeMethods.WIN32_FIND_DATA data;
            IntPtr find = NativeMethods.FindFirstFile(extendedDirectory + "\\*", out data);
            if (find != new IntPtr(-1))
            {
                try
                {
                    bool more = true;
                    while (more)
                    {
                        string name = data.cFileName;
                        if (name != "." && name != "..")
                        {
                            string child = extendedDirectory + "\\" + name;
                            bool isDirectory = (data.dwFileAttributes & FileAttributes.Directory) != 0;
                            bool isReparse = (data.dwFileAttributes & FileAttributes.ReparsePoint) != 0;
                            if (isDirectory && !isReparse) DeleteDirectoryLongPath(child);
                            else if (isDirectory)
                            {
                                NativeMethods.SetFileAttributes(child, FileAttributes.Normal);
                                if (!NativeMethods.RemoveDirectory(child)) ThrowDeleteError();
                            }
                            else
                            {
                                NativeMethods.SetFileAttributes(child, FileAttributes.Normal);
                                if (!NativeMethods.DeleteFile(child)) ThrowDeleteError();
                            }
                        }
                        more = NativeMethods.FindNextFile(find, out data);
                        if (!more)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != 18) throw new Win32Exception(error, "Long-path enumeration failed.");
                        }
                    }
                }
                finally { NativeMethods.FindClose(find); }
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 2 && error != 3) throw new Win32Exception(error, "Long-path enumeration failed.");
            }
            NativeMethods.SetFileAttributes(extendedDirectory, FileAttributes.Normal);
            if (!NativeMethods.RemoveDirectory(extendedDirectory))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 2 && error != 3) throw new Win32Exception(error, "Long-path directory removal failed.");
            }
        }

        private static void ThrowDeleteError()
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 2 && error != 3) throw new Win32Exception(error, "Long-path deletion failed.");
        }

        internal static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) return argument;
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int slashes = 0;
            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes);
                slashes = 0;
                result.Append(c);
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }
    }
}

namespace CodexPortable
{
    internal static class SignatureVerifier
    {
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        internal static bool Verify(string path)
        {
            return Verify(path, IntPtr.Zero);
        }

        internal static bool Verify(string path, FileStream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("The signature-verification stream must be readable and seekable.",
                    "stream");
            SafeFileHandle handle = stream.SafeFileHandle;
            if (handle.IsInvalid || handle.IsClosed)
                throw new ObjectDisposedException("stream");
            bool addedReference = false;
            stream.Position = 0;
            try
            {
                handle.DangerousAddRef(ref addedReference);
                return Verify(path, handle.DangerousGetHandle());
            }
            finally
            {
                if (addedReference) handle.DangerousRelease();
                stream.Position = 0;
            }
        }

        private static bool Verify(string path, IntPtr fileHandle)
        {
            NativeMethods.WINTRUST_FILE_INFO fileInfo = new NativeMethods.WINTRUST_FILE_INFO();
            fileInfo.cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.WINTRUST_FILE_INFO));
            fileInfo.pcwszFilePath = path;
            fileInfo.hFile = fileHandle;
            fileInfo.pgKnownSubject = IntPtr.Zero;

            IntPtr fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(NativeMethods.WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                NativeMethods.WINTRUST_DATA data = new NativeMethods.WINTRUST_DATA();
                data.cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.WINTRUST_DATA));
                data.dwUIChoice = 2;               // WTD_UI_NONE
                data.fdwRevocationChecks = 0;       // WTD_REVOKE_NONE
                data.dwUnionChoice = 1;             // WTD_CHOICE_FILE
                data.pFile = fileInfoPointer;
                data.dwStateAction = 0;
                data.dwProvFlags = 0;
                data.dwUIContext = 0;
                Guid action = GenericVerifyV2;
                return NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure(fileInfoPointer, typeof(NativeMethods.WINTRUST_FILE_INFO));
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }
        }
    }

    internal sealed class JobRun : IDisposable
    {
        internal const int ProcessTreeTerminationTimeoutMilliseconds = 15000;
        internal const int ProcessTreeTerminationPollMilliseconds = 50;
        internal const string DesktopJobNamePrefix = "Global\\LFPortable-DesktopJob-";
        private const string RootTokenPrefix = "root-";
        private const int RootTokenHexLength = 16;
        private readonly object sync = new object();
        private IntPtr jobHandle;
        private IntPtr processHandle;
        internal readonly uint ProcessId;
        internal readonly string JobName;
        private bool terminationRequested;

        private JobRun(IntPtr job, IntPtr process, uint processId, string jobName)
        {
            jobHandle = job;
            processHandle = process;
            ProcessId = processId;
            JobName = jobName;
        }

        internal static JobRun Start(string executable, string arguments, string workingDirectory,
            Dictionary<string, string> environment, string rootToken)
        {
            return Start(executable, arguments, workingDirectory, environment, rootToken, 0);
        }

        private static JobRun Start(string executable, string arguments, string workingDirectory,
            Dictionary<string, string> environment, string rootToken, uint additionalCreationFlags)
        {
            string jobName = CreateDesktopJobName(rootToken);
            IntPtr job = NativeMethods.CreateJobObject(IntPtr.Zero, jobName);
            int jobCreateError = Marshal.GetLastWin32Error();
            if (job == IntPtr.Zero) throw new Win32Exception(jobCreateError, "Unable to create process job.");
            if (jobCreateError == NativeMethods.ErrorAlreadyExists)
            {
                NativeMethods.CloseHandle(job);
                throw new IOException("A newly generated desktop job name already exists.");
            }
            IntPtr environmentBlock = IntPtr.Zero;
            int environmentBlockLength = 0;
            NativeMethods.PROCESS_INFORMATION processInfo = new NativeMethods.PROCESS_INFORMATION();
            bool created = false;
            try
            {
                NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                int limitsLength = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr limitsPointer = Marshal.AllocHGlobal(limitsLength);
                try
                {
                    Marshal.StructureToPtr(limits, limitsPointer, false);
                    if (!NativeMethods.SetInformationJobObject(job, 9, limitsPointer, (uint)limitsLength))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure process job.");
                }
                finally { Marshal.FreeHGlobal(limitsPointer); }

                environmentBlock = BuildEnvironmentBlock(environment, out environmentBlockLength);
                NativeMethods.STARTUPINFO startup = new NativeMethods.STARTUPINFO();
                startup.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.STARTUPINFO));
                StringBuilder command = new StringBuilder(IOUtil.QuoteArgument(executable));
                if (!string.IsNullOrEmpty(arguments)) command.Append(" ").Append(arguments);
                uint flags = 0x00000004 | 0x00000400 | additionalCreationFlags; // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT
                created = NativeMethods.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, flags,
                    environmentBlock, workingDirectory, ref startup, out processInfo);
                if (!created) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create Codex process.");
                if (!NativeMethods.AssignProcessToJobObject(job, processInfo.hProcess))
                {
                    NativeMethods.TerminateProcess(processInfo.hProcess, 1);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to contain Codex process tree.");
                }
                if (NativeMethods.ResumeThread(processInfo.hThread) == 0xFFFFFFFF)
                {
                    NativeMethods.TerminateProcess(processInfo.hProcess, 1);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume Codex process.");
                }
                NativeMethods.CloseHandle(processInfo.hThread);
                processInfo.hThread = IntPtr.Zero;
                JobRun result = new JobRun(job, processInfo.hProcess, processInfo.dwProcessId, jobName);
                job = IntPtr.Zero;
                processInfo.hProcess = IntPtr.Zero;
                return result;
            }
            finally
            {
                if (environmentBlock != IntPtr.Zero)
                {
                    byte[] zeros = new byte[environmentBlockLength];
                    Marshal.Copy(zeros, 0, environmentBlock, zeros.Length);
                    Array.Clear(zeros, 0, zeros.Length);
                    Marshal.FreeHGlobal(environmentBlock);
                }
                if (processInfo.hThread != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hThread);
                if (processInfo.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hProcess);
                if (job != IntPtr.Zero) NativeMethods.CloseHandle(job);
            }
        }

        private static IntPtr BuildEnvironmentBlock(Dictionary<string, string> environment, out int byteLength)
        {
            List<string> entries = new List<string>();
            foreach (KeyValuePair<string, string> pair in environment)
            {
                if (pair.Key.Length == 0 || pair.Key[0] == '=' || pair.Key.IndexOf('\0') >= 0 || pair.Value.IndexOf('\0') >= 0) continue;
                entries.Add(pair.Key + "=" + pair.Value);
            }
            entries.Sort(StringComparer.OrdinalIgnoreCase);
            string block = string.Join("\0", entries.ToArray()) + "\0\0";
            byte[] bytes = Encoding.Unicode.GetBytes(block);
            byteLength = bytes.Length;
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Array.Clear(bytes, 0, bytes.Length);
            return pointer;
        }

        internal static string GetDesktopJobNameForToken(string rootToken)
        {
            return CreateDesktopJobName(rootToken);
        }

        private static string CreateDesktopJobName(string rootToken)
        {
            if (!IsRootToken(rootToken)) throw new ArgumentException("rootToken");
            return DesktopJobNamePrefix + rootToken;
        }

        internal static bool TryGetRootJobStateForToken(string rootToken, out bool jobExists,
            out bool active)
        {
            jobExists = false;
            active = false;
            IntPtr job = IntPtr.Zero;
            try
            {
                job = NativeMethods.OpenJobObject(NativeMethods.JobObjectQuery, false,
                    GetDesktopJobNameForToken(rootToken));
                if (job == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    return error == 2;
                }
                jobExists = true;
                uint count;
                if (!TryGetActiveProcessCount(job, out count)) return false;
                active = count != 0;
                return true;
            }
            catch { return false; }
            finally { if (job != IntPtr.Zero) NativeMethods.CloseHandle(job); }
        }

        internal static bool IsRootToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length != RootTokenPrefix.Length +
                RootTokenHexLength || !token.StartsWith(RootTokenPrefix,
                    StringComparison.Ordinal)) return false;
            for (int i = RootTokenPrefix.Length; i < token.Length; i++)
            {
                char c = token[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static bool TryGetActiveProcessCount(IntPtr job, out uint active)
        {
            active = 0;
            if (job == IntPtr.Zero) return false;
            int size = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(size);
                uint returned;
                if (!NativeMethods.QueryInformationJobObject(job, 1, buffer, (uint)size,
                    out returned) || returned < (uint)size) return false;
                NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting =
                    (NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)Marshal.PtrToStructure(
                        buffer, typeof(NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
                active = accounting.ActiveProcesses;
                return true;
            }
            catch { return false; }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }

        private uint QueryActiveProcessCountLocked()
        {
            int size = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                uint returned;
                if (!NativeMethods.QueryInformationJobObject(jobHandle, 1, pointer,
                    (uint)size, out returned))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Unable to query Codex process-tree state.");
                }
                if (returned < (uint)size)
                    throw new InvalidDataException("Windows returned an incomplete process-tree state.");
                NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting =
                    (NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)Marshal.PtrToStructure(
                        pointer, typeof(NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
                return accounting.ActiveProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        internal void TerminateProcessTreeAndWait(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            lock (sync)
            {
                if (jobHandle == IntPtr.Zero) return;
                if (!terminationRequested)
                {
                    if (!NativeMethods.TerminateJobObject(jobHandle, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "Unable to terminate Codex process tree.");
                    terminationRequested = true;
                }

                Stopwatch timer = Stopwatch.StartNew();
                while (true)
                {
                    uint activeProcesses = QueryActiveProcessCountLocked();
                    if (activeProcesses == 0) return;
                    if (timer.ElapsedMilliseconds >= timeoutMilliseconds)
                        throw new TimeoutException("Codex process tree did not exit before the termination timeout.");
                    long remaining = timeoutMilliseconds - timer.ElapsedMilliseconds;
                    int delay = (int)Math.Min((long)ProcessTreeTerminationPollMilliseconds,
                        Math.Max(1L, remaining));
                    Thread.Sleep(delay);
                }
            }
        }

        internal void StopProcessTree()
        {
            lock (sync)
            {
                if (jobHandle == IntPtr.Zero || terminationRequested) return;
                if (!NativeMethods.TerminateJobObject(jobHandle, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Unable to terminate Codex process tree.");
                terminationRequested = true;
            }
        }

        // The launcher must not report success merely because CreateProcess
        // succeeded. A mapped-image I/O failure happens after that point and
        // otherwise looks like a successful handoff.
        internal bool TryGetEarlyExit(int timeoutMilliseconds, out uint exitCode)
        {
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            IntPtr waitHandle = IntPtr.Zero;
            try
            {
                lock (sync)
                {
                    if (processHandle == IntPtr.Zero)
                        throw new InvalidOperationException("Codex process handle is unavailable.");
                    if (!NativeMethods.DuplicateHandle(NativeMethods.GetCurrentProcess(), processHandle,
                        NativeMethods.GetCurrentProcess(), out waitHandle, 0, false,
                        NativeMethods.DuplicateSameAccess))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "Unable to duplicate the Codex process handle.");
                }
                uint result = NativeMethods.WaitForSingleObject(waitHandle,
                    unchecked((uint)timeoutMilliseconds));
                if (result == NativeMethods.WaitTimeout)
                {
                    exitCode = 0;
                    return false;
                }
                if (result != NativeMethods.WaitObject0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Unable to wait for Codex startup.");
                if (!NativeMethods.GetExitCodeProcess(waitHandle, out exitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Unable to read Codex startup exit code.");
                return true;
            }
            finally
            {
                if (waitHandle != IntPtr.Zero) NativeMethods.CloseHandle(waitHandle);
            }
        }

        internal bool TryWaitForInputIdle(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            IntPtr waitHandle = IntPtr.Zero;
            try
            {
                lock (sync)
                {
                    if (processHandle == IntPtr.Zero)
                        throw new InvalidOperationException("Codex process handle is unavailable.");
                    if (!NativeMethods.DuplicateHandle(NativeMethods.GetCurrentProcess(), processHandle,
                        NativeMethods.GetCurrentProcess(), out waitHandle, 0, false,
                        NativeMethods.DuplicateSameAccess))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "Unable to duplicate the Codex process handle.");
                }
                // WaitForInputIdle returns zero once a GUI process has created
                // its message queue, 0x102 on timeout, and 0xffffffff when the
                // target is not a GUI process or the handle is unavailable.
                return NativeMethods.WaitForInputIdle(waitHandle,
                    unchecked((uint)timeoutMilliseconds)) == NativeMethods.WaitObject0;
            }
            finally
            {
                if (waitHandle != IntPtr.Zero) NativeMethods.CloseHandle(waitHandle);
            }
        }

        internal bool TryDetachAfterStartup(out uint exitCode)
        {
            lock (sync)
            {
                exitCode = 0;
                if (jobHandle == IntPtr.Zero || processHandle == IntPtr.Zero)
                    throw new InvalidOperationException("Codex process ownership is unavailable.");
                uint wait = NativeMethods.WaitForSingleObject(processHandle, 0);
                if (wait == NativeMethods.WaitObject0)
                {
                    if (!NativeMethods.GetExitCodeProcess(processHandle, out exitCode))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "Unable to read Codex startup exit code.");
                    return false;
                }
                if (wait != NativeMethods.WaitTimeout)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Unable to confirm Codex startup.");
                NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits =
                    new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0;
                int size = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr pointer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, pointer, false);
                    if (!NativeMethods.SetInformationJobObject(jobHandle, 9, pointer, (uint)size))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "Unable to detach Codex process tree.");
                }
                finally { Marshal.FreeHGlobal(pointer); }
                NativeMethods.CloseHandle(jobHandle);
                jobHandle = IntPtr.Zero;
                if (processHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(processHandle);
                    processHandle = IntPtr.Zero;
                }
                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (jobHandle != IntPtr.Zero)
                {
                    if (!terminationRequested)
                    {
                        NativeMethods.TerminateJobObject(jobHandle, 0);
                        terminationRequested = true;
                    }
                    NativeMethods.CloseHandle(jobHandle);
                    jobHandle = IntPtr.Zero;
                }
                if (processHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(processHandle);
                    processHandle = IntPtr.Zero;
                }
            }
        }
    }

    internal static class NativeMethods
    {
        internal const uint InvalidFileAttributes = 0xFFFFFFFF;
        internal static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint FileShareDelete = 0x00000004;
        internal const uint CreateNew = 1;
        internal const uint CreateAlways = 2;
        internal const uint OpenExisting = 3;
        internal const uint OpenAlways = 4;
        internal const uint TruncateExisting = 5;
        internal const uint FileAttributeNormal = 0x00000080;
        internal const uint FileFlagWriteThrough = 0x80000000;
        internal const uint FileFlagSequentialScan = 0x08000000;
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint JobObjectQuery = 0x0004;
        internal const uint MaximumProcessImagePath = 32768;
        internal const uint WaitObject0 = 0;
        internal const uint WaitTimeout = 258;
        internal const uint Infinite = 0xFFFFFFFF;
        internal const uint DuplicateSameAccess = 0x00000002;
        internal const int ErrorAlreadyExists = 183;
        internal const uint SemFailCriticalErrors = 0x0001;
        internal const uint SemNoGpFaultErrorBox = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_INFO
        {
            internal ushort wProcessorArchitecture;
            internal ushort wReserved;
            internal uint dwPageSize;
            internal IntPtr lpMinimumApplicationAddress;
            internal IntPtr lpMaximumApplicationAddress;
            internal UIntPtr dwActiveProcessorMask;
            internal uint dwNumberOfProcessors;
            internal uint dwProcessorType;
            internal uint dwAllocationGranularity;
            internal ushort wProcessorLevel;
            internal ushort wProcessorRevision;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WINTRUST_FILE_INFO
        {
            internal uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] internal string pcwszFilePath;
            internal IntPtr hFile;
            internal IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WINTRUST_DATA
        {
            internal uint cbStruct;
            internal IntPtr pPolicyCallbackData;
            internal IntPtr pSIPClientData;
            internal uint dwUIChoice;
            internal uint fdwRevocationChecks;
            internal uint dwUnionChoice;
            internal IntPtr pFile;
            internal uint dwStateAction;
            internal IntPtr hWVTStateData;
            internal IntPtr pwszURLReference;
            internal uint dwProvFlags;
            internal uint dwUIContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFO
        {
            internal uint cb;
            internal IntPtr lpReserved;
            internal IntPtr lpDesktop;
            internal IntPtr lpTitle;
            internal uint dwX;
            internal uint dwY;
            internal uint dwXSize;
            internal uint dwYSize;
            internal uint dwXCountChars;
            internal uint dwYCountChars;
            internal uint dwFillAttribute;
            internal uint dwFlags;
            internal ushort wShowWindow;
            internal ushort cbReserved2;
            internal IntPtr lpReserved2;
            internal IntPtr hStdInput;
            internal IntPtr hStdOutput;
            internal IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            internal IntPtr hProcess;
            internal IntPtr hThread;
            internal uint dwProcessId;
            internal uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TOKEN_ELEVATION
        {
            internal uint TokenIsElevated;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            internal JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            internal IO_COUNTERS IoInfo;
            internal UIntPtr ProcessMemoryLimit;
            internal UIntPtr JobMemoryLimit;
            internal UIntPtr PeakProcessMemoryUsed;
            internal UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            internal ulong TotalUserTime;
            internal ulong TotalKernelTime;
            internal ulong ThisPeriodTotalUserTime;
            internal ulong ThisPeriodTotalKernelTime;
            internal uint TotalPageFaultCount;
            internal uint TotalProcesses;
            internal uint ActiveProcesses;
            internal uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WIN32_FIND_DATA
        {
            internal FileAttributes dwFileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            internal uint nFileSizeHigh;
            internal uint nFileSizeLow;
            internal uint dwReserved0;
            internal uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] internal string cAlternateFileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WIN32_FILE_ATTRIBUTE_DATA
        {
            internal FileAttributes dwFileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            internal uint nFileSizeHigh;
            internal uint nFileSizeLow;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid actionId, [In] ref WINTRUST_DATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
            IntPtr information, uint informationLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenJobObject(uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
            string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint WaitForInputIdle(IntPtr process, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll")]
        internal static extern uint SetErrorMode(uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(IntPtr sourceProcessHandle,
            IntPtr sourceHandle, IntPtr targetProcessHandle, out IntPtr targetHandle,
            uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(IntPtr process,
            out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(IntPtr processHandle, uint flags,
            StringBuilder executablePath, ref uint size);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process2(IntPtr process, out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("kernel32.dll")]
        internal static extern void GetNativeSystemInfo(out SYSTEM_INFO systemInfo);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            out TOKEN_ELEVATION tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstFile(string fileName, out WIN32_FIND_DATA findFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectory(string pathName, IntPtr securityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFileAttributes(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileAttributesEx(string name, int infoLevel,
            out WIN32_FILE_ATTRIBUTE_DATA fileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformation(string rootPathName,
            StringBuilder volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber,
            out uint maximumComponentLength, out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer, uint fileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileTime(SafeFileHandle file, IntPtr creationTime,
            IntPtr lastAccessTime, ref System.Runtime.InteropServices.ComTypes.FILETIME lastWriteTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CopyFile(string existingFileName, string newFileName,
            [MarshalAs(UnmanagedType.Bool)] bool failIfExists);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFile(string existingFileName, string newFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextFile(IntPtr findFile, out WIN32_FIND_DATA findFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr findFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteFile(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveDirectory(string pathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileAttributes(string fileName, FileAttributes fileAttributes);
    }
}

namespace CodexPortable
{
    internal static class ProviderConfiguration
    {
        internal const string ProviderId = "portable_custom";
        internal const string ApiKeyEnvironmentVariable = "CODEX_PORTABLE_API_KEY";
        internal const string DefaultApprovalPolicy = "never";
        internal const string DefaultSandboxMode = "danger-full-access";
        internal const string DefaultReasoningEffort = "max";
        internal const string DefaultModel = "gpt-5.6-terra";
        internal const string DefaultFollowUpQueueMode = "steer";
        private const string PiModelsUrl = "https://pi.dev/api/models";
        private const string PiCacheResourceName = "CodexPortable.PiModelsCache.json";
        private const int ModelCatalogMaximumBytes = 16 * 1024 * 1024;
        private const int ModelCatalogTimeoutMilliseconds = 8000;
        private const int BundledModelTemplateTimeoutMilliseconds = 30000;
        private const int BundledModelTemplateMaximumBytes = 16 * 1024 * 1024;
        private const int ModelCatalogMaximumCount = 2000;
        private sealed class PiModelCandidate
        {
            internal string Provider;
            internal Dictionary<string, object> Metadata;
        }
        // Used only when a gateway model has no model-specific instructions
        // from the gateway, pi.dev, or the bundled CLI catalog.
        private const string MinimalModelInstructions =
            "You are Codex, a coding agent. You and the user share one workspace and collaborate to achieve the user's goals.\n\n" +
            "Be precise, safe, and helpful. Inspect the available context before acting, keep changes focused, and verify the result.";
        internal static readonly string DefaultModelInstructions = ReadFallbackPrompt();
        internal const string DefaultDeveloperInstructions =
            "Codex Portable 默认规则：\n" +
            "1. 不编写任何 checkpoint 或 hash 相关代码，避免流程扩大或复杂化。\n" +
            "2. 不保留兼容性代码或历史性代码，直接实现当前目标。\n" +
            "3. 所需工具统一安装并使用，不因工具未安装而绕过流程或引入额外复杂步骤。";
        internal static string ApprovalPolicyConfigLine { get { return "approval_policy = " + QuoteToml(DefaultApprovalPolicy); } }
        internal static string SandboxModeConfigLine { get { return "sandbox_mode = " + QuoteToml(DefaultSandboxMode); } }
        internal static string ReasoningEffortConfigLine { get { return "model_reasoning_effort = " + QuoteToml(DefaultReasoningEffort); } }
        internal static string DeveloperInstructionsConfigLine { get { return "developer_instructions = " + QuoteToml(DefaultDeveloperInstructions); } }
        private const string UnconfiguredBaseUrl = "https://invalid.invalid/v1";
        private const string SecretExcludes = "[\"OPENAI_API_KEY\", \"CODEX_API_KEY\", \"CODEX_PORTABLE_API_KEY\", \"OPENAI_BASE_URL\", \"CODEX_APP_SERVER_OPENAI_BASE_URL\", \"ANTHROPIC_API_KEY\", \"AZURE_OPENAI_API_KEY\", \"AWS_ACCESS_KEY_ID\", \"AWS_SECRET_ACCESS_KEY\", \"AWS_SESSION_TOKEN\", \"GITHUB_TOKEN\", \"GH_TOKEN\"]";
        private static readonly string[] X64BundledPluginNames = new string[] {
            "sites", "browser", "chrome", "computer-use", "codex-app-tools", "latex", "deep-research",
            "unified-computer-use", "user-writing", "visualize"
        };
        private static readonly string[] Arm64BundledPluginNames = new string[] {
            "sites", "browser", "chrome", "computer-use", "codex-app-tools", "deep-research",
            "unified-computer-use", "user-writing", "visualize"
        };
        private static readonly string[] PrimaryRuntimePluginNames = new string[] {
            "documents", "pdf", "presentations", "spreadsheets", "template-creator"
        };
        private static readonly string[] NoRequiredPlugins = new string[0];
        private static readonly string[] X64RequiredPlugins = BuildRequiredPlugins(X64BundledPluginNames);
        private static readonly string[] Arm64RequiredPlugins = BuildRequiredPlugins(Arm64BundledPluginNames);

        private static string ReadFallbackPrompt()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                    "CodexPortable.ModelFallbackPrompt.txt"))
                {
                    if (stream == null || stream.Length <= 0 || stream.Length > 512 * 1024)
                        return MinimalModelInstructions;
                    using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true),
                        true, 4096))
                    {
                        string value = reader.ReadToEnd().Trim();
                        return value.Length == 0 ? MinimalModelInstructions : value;
                    }
                }
            }
            catch { return MinimalModelInstructions; }
        }

        private static string[] BuildRequiredPlugins(string[] bundledPluginNames)
        {
            string[] result = new string[bundledPluginNames.Length + PrimaryRuntimePluginNames.Length];
            int index = 0;
            for (int i = 0; i < bundledPluginNames.Length; i++)
                result[index++] = bundledPluginNames[i] + "@openai-bundled";
            for (int i = 0; i < PrimaryRuntimePluginNames.Length; i++)
                result[index++] = PrimaryRuntimePluginNames[i] + "@openai-primary-runtime";
            return result;
        }

        private static string[] SelectRequiredPlugins(PortableArchitecture architecture)
        {
            switch (architecture)
            {
                case PortableArchitecture.X64: return X64RequiredPlugins;
                case PortableArchitecture.Arm64: return Arm64RequiredPlugins;
                default: return NoRequiredPlugins;
            }
        }

        private static string[] SelectRequiredBundledPluginNames(PortableArchitecture architecture)
        {
            switch (architecture)
            {
                case PortableArchitecture.X64: return X64BundledPluginNames;
                case PortableArchitecture.Arm64: return Arm64BundledPluginNames;
                default:
                    throw new InvalidDataException("No official bundled-plugin contract exists for architecture: " +
                        ArchitectureInfo.NameOf(architecture));
            }
        }

        internal static string[] GetRequiredPlugins(PortableArchitecture architecture)
        {
            return (string[])SelectRequiredPlugins(architecture).Clone();
        }

        // The desktop payload is extracted after the first config write. Keep
        // the architecture lists above as the pre-extraction fallback, then
        // use the signed payload's actual marketplace inventory on subsequent
        // writes so new official plugins do not require a launcher patch.
        internal static string[] GetRequiredPlugins(PortableLayout layout)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            if (!ArchitectureInfo.HasOfficialDesktopPayload(layout.Architecture))
                return (string[])NoRequiredPlugins.Clone();
            return BuildRequiredPlugins(GetBundledPluginNames(layout));
        }

        internal static string[] GetRequiredBundledPluginNames(PortableArchitecture architecture)
        {
            return (string[])SelectRequiredBundledPluginNames(architecture).Clone();
        }

        internal static string[] GetBundledPluginNames(PortableLayout layout)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            string pluginsRoot = Path.Combine(layout.Resources, "plugins",
                "openai-bundled", "plugins");
            if (File.Exists(layout.OfficialAppExe))
                return DiscoverBundledPluginNames(pluginsRoot);
            return GetRequiredBundledPluginNames(layout.Architecture);
        }

        // This is intentionally fail-closed for an already extracted payload.
        // Config generation uses the Try* wrapper above so a missing directory
        // during first launch still falls back to the architecture defaults.
        internal static string[] DiscoverBundledPluginNames(string pluginsRoot)
        {
            string[] discovered;
            if (!TryDiscoverBundledPluginNames(pluginsRoot, out discovered))
                throw new InvalidDataException("Official bundled-plugin inventory is missing or invalid: " + pluginsRoot);
            return discovered;
        }

        private static bool TryDiscoverBundledPluginNames(string pluginsRoot, out string[] names)
        {
            names = null;
            try
            {
                if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot) ||
                    (File.GetAttributes(pluginsRoot) & FileAttributes.ReparsePoint) != 0) return false;
                string[] entries = Directory.GetFileSystemEntries(pluginsRoot, "*",
                    SearchOption.TopDirectoryOnly);
                if (entries.Length == 0) return false;
                List<string> discovered = new List<string>(entries.Length);
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < entries.Length; i++)
                {
                    FileAttributes attributes = File.GetAttributes(entries[i]);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                        (attributes & FileAttributes.Directory) == 0) return false;
                    string pluginName = Path.GetFileName(entries[i]);
                    if (!IsSafePluginName(pluginName) || !seen.Add(pluginName)) return false;

                    string metadataDirectory = Path.Combine(entries[i], ".codex-plugin");
                    string manifest = Path.Combine(metadataDirectory, "plugin.json");
                    if (!Directory.Exists(metadataDirectory) ||
                        (File.GetAttributes(metadataDirectory) & FileAttributes.ReparsePoint) != 0 ||
                        !File.Exists(manifest) ||
                        (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
                        return false;
                    string manifestName;
                    string version;
                    PluginCacheRecovery.ReadManifestIdentity(manifest, out manifestName, out version);
                    if (!string.Equals(manifestName, pluginName, StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(version)) return false;
                    discovered.Add(pluginName);
                }
                discovered.Sort(StringComparer.Ordinal);
                names = discovered.ToArray();
                return true;
            }
            catch
            {
                names = null;
                return false;
            }
        }

        private static bool IsSafePluginName(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "." || value == ".." || value.Length > 128)
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.')) return false;
            }
            return true;
        }

        internal static bool TryNormalizeBaseUrl(string input, out string normalized)
        {
            normalized = null;
            string value = (input ?? "").Trim();
            if (value.Length == 0 || value.Length > 2048) return false;
            if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\0') >= 0) return false;
            // Accept a bare host (lv.lifaplus.com) and host:port forms by
            // defaulting to HTTPS when no scheme was typed.
            if (value.IndexOf("://", StringComparison.Ordinal) < 0)
                value = "https://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
            bool https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            bool loopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback;
            if (!https && !loopbackHttp) return false;
            if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            // Strip a full endpoint the user may have pasted (…/v1/chat/completions,
            // …/chat/completions, …/v1/responses, …/responses, …/completions) back
            // to the API base so both the /models probe and the desktop requests
            // built later on this value stay consistent for every URL shape.
            string basePath = uri.AbsolutePath.TrimEnd('/');
            string lowerPath = basePath.ToLowerInvariant();
            if (lowerPath.EndsWith("/chat/completions"))
                basePath = basePath.Substring(0,
                    basePath.Length - "/chat/completions".Length);
            else if (lowerPath.EndsWith("/responses"))
                basePath = basePath.Substring(0, basePath.Length - "/responses".Length);
            else if (lowerPath.EndsWith("/completions"))
                basePath = basePath.Substring(0, basePath.Length - "/completions".Length);
            normalized = (uri.Scheme + "://" + uri.Authority + basePath).TrimEnd('/');
            return normalized.Length > 0;
        }

        internal static bool IsValidModel(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 512) return false;
            for (int i = 0; i < value.Length; i++) if (char.IsWhiteSpace(value[i]) || char.IsControl(value[i])) return false;
            return true;
        }

        internal static bool IsValidApiKey(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 1024) return false;
            for (int i = 0; i < value.Length; i++) if (char.IsWhiteSpace(value[i]) || char.IsControl(value[i])) return false;
            return true;
        }

        internal static List<string> ReadCatalogModelIds(PortableLayout layout)
        {
            List<string> result = new List<string>();
            try
            {
                if (layout == null || !File.Exists(layout.ModelCatalogFile)) return result;
                FileInfo info = new FileInfo(layout.ModelCatalogFile);
                if (info.Length <= 0 || info.Length > ModelCatalogMaximumBytes) return result;
                Dictionary<string, object> root = ParseJsonObject(File.ReadAllText(
                    layout.ModelCatalogFile, Encoding.UTF8), ModelCatalogMaximumBytes);
                object modelsValue;
                object[] models;
                if (!root.TryGetValue("models", out modelsValue) ||
                    (models = ToObjectArray(modelsValue)) == null) return result;
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < models.Length; i++)
                {
                    Dictionary<string, object> model = ToObjectDictionary(models[i]);
                    string id = GetString(model, "slug");
                    if (IsValidModel(id) && seen.Add(id)) result.Add(id);
                }
            }
            catch { result.Clear(); }
            return result;
        }

        internal static bool TryDiscoverApiBase(string input, string apiKey,
            out string workingBase, out List<string> modelIds)
        {
            workingBase = null;
            modelIds = null;
            string normalized;
            if (!TryNormalizeBaseUrl(input, out normalized)) return false;
            List<string> candidates = new List<string>();
            candidates.Add(normalized);
            try
            {
                Uri uri = new Uri(normalized, UriKind.Absolute);
                if (uri.AbsolutePath.TrimEnd('/').Length == 0)
                {
                    // Host-only inputs usually belong to an OpenAI-compatible
                    // gateway whose API lives under /v1; probe it as a fallback
                    // when the origin itself does not answer /models.
                    candidates.Add(normalized + "/v1");
                }
            }
            catch { }
            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    List<Dictionary<string, object>> models =
                        FetchGatewayModels(candidates[i], apiKey);
                    List<string> ids = new List<string>();
                    for (int j = 0; j < models.Count; j++)
                        ids.Add(GetModelId(models[j]));
                    workingBase = candidates[i];
                    modelIds = ids;
                    return true;
                }
                catch { }
            }
            return false;
        }

        internal static List<string> FetchGatewayModelIds(string baseUrl, string apiKey)
        {
            List<Dictionary<string, object>> models = FetchGatewayModels(baseUrl, apiKey);
            List<string> result = new List<string>();
            for (int i = 0; i < models.Count; i++) result.Add(GetModelId(models[i]));
            return result;
        }

        private static List<Dictionary<string, object>> FetchGatewayModels(string baseUrl,
            string apiKey)
        {
            string normalized;
            if (!TryNormalizeBaseUrl(baseUrl, out normalized))
                throw new InvalidDataException("Invalid custom API base URL.");
            if (!IsValidApiKey(apiKey)) throw new InvalidDataException("Invalid custom API key.");
            object root = DownloadJsonValue(BuildModelsUrl(normalized), apiKey,
                ModelCatalogMaximumBytes);
            Dictionary<string, object> rootObject = ToObjectDictionary(root);
            if (rootObject != null) return ReadGatewayModels(rootObject);
            object[] rootArray = ToJsonArray(root);
            if (rootArray == null)
                throw new InvalidDataException("The custom API model response is not an object or array.");
            return ReadGatewayModels(new Dictionary<string, object> {
                { "data", rootArray }
            });
        }

        internal static void EnsureOfflineFallbackCatalog(PortableLayout layout,
            string model)
        {
            // A first run must not become unusable because the gateway is
            // unreachable.  When no previous catalog exists, materialize a
            // minimal offline catalog from the configured model so Codex can
            // start and retry the gateway refresh on later starts.  The
            // desktop cannot send messages without the gateway, but the UI and
            // the configured model stay available.
            if (layout == null || !IsValidModel(model)) return;
            if (ReadCatalogModelIds(layout).Count != 0) return;
            List<object> models = new List<object>();
            Dictionary<string, object> gateway = new Dictionary<string, object>(
                StringComparer.Ordinal);
            gateway["id"] = model;
            // An offline first run must still expose the model's reasoning
            // presets; without them the desktop defaults to a mid tier and
            // shows no max dial.  The configured default model advertises the
            // full set including the portable default effort.
            gateway["reasoning"] = true;
            bool isDefaultModel = string.Equals(model, DefaultModel,
                StringComparison.Ordinal);
            string[] offlinePresets = isDefaultModel ?
                new string[] { "low", "medium", "high", "max" } :
                new string[] { "low", "medium", "high" };
            List<object> offlineLevels = new List<object>();
            for (int i = 0; i < offlinePresets.Length; i++)
            {
                Dictionary<string, object> level = new Dictionary<string, object>(
                    StringComparer.Ordinal);
                level["effort"] = offlinePresets[i];
                level["description"] = ReasoningDescription(offlinePresets[i]);
                offlineLevels.Add(level);
            }
            gateway["supported_reasoning_levels"] = offlineLevels.ToArray();
            if (isDefaultModel) gateway["default_reasoning_level"] = "max";
            models.Add(CreateCodexModelInfo(model, gateway, null, null,
                DefaultModelInstructions, 1));
            WriteModelCatalog(layout, models.ToArray());
            IOUtil.AtomicWriteText(layout.ModelFile, model + "\r\n");
        }

        private static void PersistPiCache(PortableLayout layout,
            Dictionary<string, object> piRoot)
        {
            try
            {
                if (layout == null || piRoot == null || piRoot.Count == 0) return;
                layout.EnsureDirectories();
                string json = CreateJsonSerializer(ModelCatalogMaximumBytes).Serialize(piRoot);
                if (Encoding.UTF8.GetByteCount(json) > ModelCatalogMaximumBytes) return;
                IOUtil.AtomicWriteText(layout.PiCacheFile, json);
            }
            catch
            {
                // A cache write must never block model catalog refresh.
            }
        }

        private static Dictionary<string, object> ReadPiCache(PortableLayout layout)
        {
            // 1) newest disk cache written by an earlier successful refresh,
            // 2) the snapshot embedded into this release,
            // 3) none.
            if (layout != null && File.Exists(layout.PiCacheFile))
            {
                try
                {
                    FileInfo info = new FileInfo(layout.PiCacheFile);
                    if (info.Length > 0 && info.Length <= ModelCatalogMaximumBytes)
                    {
                        Dictionary<string, object> cached = ParseJsonObject(
                            File.ReadAllText(layout.PiCacheFile, Encoding.UTF8),
                            ModelCatalogMaximumBytes);
                        if (cached != null && cached.Count != 0) return cached;
                    }
                }
                catch { }
            }
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(PiCacheResourceName))
                {
                    if (stream == null || stream.Length <= 0 ||
                        stream.Length > ModelCatalogMaximumBytes) return null;
                    using (StreamReader reader = new StreamReader(stream,
                        new UTF8Encoding(false, true), true, 4096))
                    {
                        Dictionary<string, object> embedded = ParseJsonObject(
                            reader.ReadToEnd(), ModelCatalogMaximumBytes);
                        return embedded != null && embedded.Count != 0 ? embedded : null;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        internal static int RefreshModelCatalog(PortableLayout layout, string baseUrl,
            string apiKey, string selectedModel)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            List<Dictionary<string, object>> gatewayModels = FetchGatewayModels(baseUrl, apiKey);
            List<string> modelIds = new List<string>();
            for (int i = 0; i < gatewayModels.Count; i++) modelIds.Add(GetModelId(gatewayModels[i]));
            if (modelIds.Count == 0)
            {
                // An empty successful response is authoritative, but the
                // Codex model manager cannot start with an empty ModelsResponse.
                // Remove the old catalog so stale models cannot be used as a
                // fallback after the gateway explicitly reports none.
                IOUtil.DeleteFileIfExists(layout.ModelCatalogFile);
                return 0;
            }

            Dictionary<string, object> piRoot = null;
            try
            {
                piRoot = DownloadJsonObject(PiModelsUrl, null, ModelCatalogMaximumBytes);
                PersistPiCache(layout, piRoot);
            }
            catch
            {
                // pi.dev is enrichment data. Gateway IDs remain authoritative
                // when the public metadata service is unavailable; use the
                // newest cached snapshot so enrichment still works offline.
                piRoot = ReadPiCache(layout);
            }
            Dictionary<string, List<PiModelCandidate>> piIndex =
                BuildPiModelIndex(piRoot);
            Dictionary<string, Dictionary<string, object>> bundledTemplates =
                ReadBundledModelTemplates(layout);
            if (bundledTemplates.Count == 0)
                bundledTemplates = ReadCachedModelInstructions(layout);
            // A model without an exact CLI template must not inherit another
            // model's personality or prompt. Gateway/pi metadata may provide
            // an explicit template; otherwise use the neutral fallback below.
            string defaultBaseInstructions = DefaultModelInstructions;
            List<object> models = new List<object>();
            for (int i = 0; i < modelIds.Count; i++)
            {
                Dictionary<string, object> metadata = SelectPiMetadata(modelIds[i],
                    baseUrl, gatewayModels[i], piIndex);
                Dictionary<string, object> bundledTemplate = SelectBundledModelTemplate(
                    modelIds[i], bundledTemplates);
                models.Add(CreateCodexModelInfo(modelIds[i], gatewayModels[i], metadata,
                    bundledTemplate, defaultBaseInstructions, i + 1));
            }
            WriteModelCatalog(layout, models.ToArray());
            SelectFirstCatalogModelIfMissing(layout, selectedModel, modelIds);
            return modelIds.Count;
        }

        private static void WriteModelCatalog(PortableLayout layout, object[] models)
        {
            Dictionary<string, object> catalog = new Dictionary<string, object>(
                StringComparer.Ordinal);
            catalog["models"] = models ?? new object[0];
            JavaScriptSerializer serializer = CreateJsonSerializer(ModelCatalogMaximumBytes);
            string json = serializer.Serialize(catalog);
            if (Encoding.UTF8.GetByteCount(json) > ModelCatalogMaximumBytes)
                throw new InvalidDataException("Generated model catalog is too large.");
            layout.EnsureDirectories();
            IOUtil.AtomicWriteText(layout.ModelCatalogFile, json);
        }

        internal static void SelectFirstCatalogModelIfMissing(PortableLayout layout,
            string selectedModel, IList<string> modelIds)
        {
            if (layout == null || modelIds == null || modelIds.Count == 0) return;
            for (int i = 0; i < modelIds.Count; i++)
                if (string.Equals(modelIds[i], selectedModel, StringComparison.Ordinal)) return;
            IOUtil.AtomicWriteText(layout.ModelFile, modelIds[0] + "\r\n");
        }

        private static string BuildModelsUrl(string baseUrl)
        {
            string value = (baseUrl ?? "").Trim().TrimEnd('/');
            if (value.Length == 0) return value;
            string lower = value.ToLowerInvariant();
            // Accept every shape users paste: plain origin, an origin with the
            // API version (…/v1), or a full endpoint (…/v1/chat/completions,
            // …/chat/completions, …/v1/responses, …/responses).  The /models
            // probe is derived from the API base, so known full-endpoint tails
            // are stripped back to that base first.
            if (lower.EndsWith("/chat/completions"))
                value = value.Substring(0, value.Length - "/chat/completions".Length);
            else if (lower.EndsWith("/responses"))
                value = value.Substring(0, value.Length - "/responses".Length);
            else if (lower.EndsWith("/completions"))
                value = value.Substring(0, value.Length - "/completions".Length);
            return value.TrimEnd('/') + "/models";
        }

        private static Dictionary<string, Dictionary<string, object>> ReadBundledModelTemplates(
            PortableLayout layout)
        {
            Dictionary<string, Dictionary<string, object>> result =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            try
            {
                if (layout == null || string.IsNullOrEmpty(layout.CodexExe) ||
                    !File.Exists(layout.CodexExe)) return result;
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = layout.CodexExe;
                startInfo.Arguments = "debug models --bundled";
                startInfo.WorkingDirectory = layout.CurrentApp;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                // codex debug models --bundled always writes UTF-8 JSON; the
                // default StreamReader encoding follows the console codepage
                // and would corrupt the payload on CJK systems.
                startInfo.StandardOutputEncoding = new UTF8Encoding(false);
                startInfo.RedirectStandardError = false;
                startInfo.EnvironmentVariables["CODEX_HOME"] = layout.CodexHome;
                startInfo.EnvironmentVariables["HOME"] = layout.Profile;
                startInfo.EnvironmentVariables["USERPROFILE"] = layout.Profile;
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null) return result;
                    Task<string> outputTask = Task.Run(delegate
                    {
                        return ReadLimitedText(process.StandardOutput,
                            BundledModelTemplateMaximumBytes);
                    });
                    if (!process.WaitForExit(BundledModelTemplateTimeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        return result;
                    }
                    string output = outputTask.Result;
                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return result;
                    Dictionary<string, object> root = ParseJsonObject(output,
                        BundledModelTemplateMaximumBytes);
                    object modelsValue;
                    object[] models = root.TryGetValue("models", out modelsValue) ?
                        ToObjectArray(modelsValue) : null;
                    if (models == null || models.Length == 0) return result;
                    for (int i = 0; i < models.Length; i++)
                    {
                        Dictionary<string, object> candidate = ToObjectDictionary(models[i]);
                        if (candidate == null) continue;
                        string slug = GetString(candidate, "slug");
                        if (IsValidModel(slug) && !result.ContainsKey(slug))
                            result[slug] = candidate;
                    }
                    return result;
                }
            }
            catch
            {
                result.Clear();
                return result;
            }
        }

        private static Dictionary<string, Dictionary<string, object>> ReadCachedModelInstructions(
            PortableLayout layout)
        {
            Dictionary<string, Dictionary<string, object>> result =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            try
            {
                if (layout == null || !File.Exists(layout.ModelCatalogFile)) return result;
                FileInfo info = new FileInfo(layout.ModelCatalogFile);
                if (info.Length <= 0 || info.Length > ModelCatalogMaximumBytes) return result;
                Dictionary<string, object> root = ParseJsonObject(File.ReadAllText(
                    layout.ModelCatalogFile, Encoding.UTF8), ModelCatalogMaximumBytes);
                object[] models = GetArray(root, "models");
                if (models == null) return result;
                for (int i = 0; i < models.Length; i++)
                {
                    Dictionary<string, object> candidate = ToObjectDictionary(models[i]);
                    string slug = GetString(candidate, "slug");
                    if (!IsValidModel(slug) || result.ContainsKey(slug)) continue;
                    Dictionary<string, object> cachedMessages = GetObject(candidate,
                        "model_messages");
                    string instructions = NormalizeCatalogText(
                        GetString(cachedMessages, "instructions_template") ??
                        GetString(candidate, "base_instructions"), 512 * 1024);
                    if (string.IsNullOrEmpty(instructions)) continue;
                    result[slug] = new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "slug", slug },
                        { "base_instructions", instructions },
                        { "model_messages", new Dictionary<string, object>(StringComparer.Ordinal) {
                            { "instructions_template", instructions }
                        }}
                    };
                }
            }
            catch { result.Clear(); }
            return result;
        }

        private static Dictionary<string, object> SelectBundledModelTemplate(string modelId,
            Dictionary<string, Dictionary<string, object>> templates)
        {
            if (templates == null || string.IsNullOrEmpty(modelId)) return null;
            Dictionary<string, object> result;
            if (templates.TryGetValue(modelId, out result)) return result;
            result = FindLongestBundledModelPrefix(modelId, templates);
            if (result != null) return result;
            int slash = modelId.IndexOf('/');
            if (slash >= 0 && slash + 1 < modelId.Length &&
                modelId.IndexOf('/', slash + 1) < 0)
                return FindLongestBundledModelPrefix(modelId.Substring(slash + 1), templates);
            return null;
        }

        private static Dictionary<string, object> FindLongestBundledModelPrefix(string modelId,
            Dictionary<string, Dictionary<string, object>> templates)
        {
            Dictionary<string, object> result = null;
            int length = -1;
            foreach (KeyValuePair<string, Dictionary<string, object>> entry in templates)
            {
                if (modelId.StartsWith(entry.Key, StringComparison.Ordinal) &&
                    entry.Key.Length > length)
                {
                    result = entry.Value;
                    length = entry.Key.Length;
                }
            }
            return result;
        }

        private static string SelectBundledDefaultBaseInstructions(
            Dictionary<string, Dictionary<string, object>> templates)
        {
            if (templates == null || templates.Count == 0) return null;
            Dictionary<string, object> preferred;
            if (templates.TryGetValue(DefaultModel, out preferred))
            {
                string instructions = GetString(preferred, "base_instructions");
                if (!string.IsNullOrEmpty(instructions)) return instructions;
            }

            string selected = null;
            long selectedPriority = Int64.MaxValue;
            foreach (KeyValuePair<string, Dictionary<string, object>> entry in templates)
            {
                Dictionary<string, object> candidate = entry.Value;
                string instructions = GetString(candidate, "base_instructions");
                if (string.IsNullOrEmpty(instructions) ||
                    !GetBoolean(candidate, "supported_in_api", true) ||
                    !string.Equals(GetString(candidate, "visibility"), "list",
                        StringComparison.Ordinal)) continue;
                long priority = GetPositiveInteger(candidate, "priority");
                if (priority <= 0) priority = Int64.MaxValue - 1;
                if (selected == null || priority < selectedPriority)
                {
                    selected = instructions;
                    selectedPriority = priority;
                }
            }
            if (!string.IsNullOrEmpty(selected)) return selected;
            foreach (KeyValuePair<string, Dictionary<string, object>> entry in templates)
            {
                selected = GetString(entry.Value, "base_instructions");
                if (!string.IsNullOrEmpty(selected)) return selected;
            }
            return null;
        }

        private static string ReadLimitedText(StreamReader reader, int maximumBytes)
        {
            StringBuilder text = new StringBuilder();
            char[] chunk = new char[8192];
            int read;
            while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (text.Length + read > maximumBytes)
                    throw new InvalidDataException("Bundled model catalog is too large.");
                text.Append(chunk, 0, read);
            }
            return text.ToString();
        }

        private static Dictionary<string, object> DownloadJsonObject(string url,
            string apiKey, int maximumBytes)
        {
            Dictionary<string, object> result = ToObjectDictionary(
                DownloadJsonValue(url, apiKey, maximumBytes));
            if (result == null)
                throw new InvalidDataException("Model response is not a JSON object.");
            return result;
        }

        private static object DownloadJsonValue(string url, string apiKey, int maximumBytes)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Timeout = ModelCatalogTimeoutMilliseconds;
            request.ReadWriteTimeout = ModelCatalogTimeoutMilliseconds;
            request.UserAgent = "LFPortable/" + Assembly.GetExecutingAssembly().GetName().Version;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            // Do not follow an untrusted redirect with the caller's bearer
            // token. A redirected endpoint can be treated as a failed fetch;
            // the previous catalog remains available to the caller.
            request.AllowAutoRedirect = false;
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    throw new WebException("Model endpoint returned HTTP " +
                        ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                if (response.ContentLength > maximumBytes)
                    throw new InvalidDataException("Model response is too large.");
                using (Stream stream = response.GetResponseStream())
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (buffer.Length + read > maximumBytes)
                            throw new InvalidDataException("Model response is too large.");
                        buffer.Write(chunk, 0, read);
                    }
                    string json = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                    return ParseJsonValue(json, maximumBytes);
                }
            }
        }

        private static JavaScriptSerializer CreateJsonSerializer(int maximumBytes)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = maximumBytes;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private static Dictionary<string, object> ParseJsonObject(string json,
            int maximumBytes)
        {
            Dictionary<string, object> result = ToObjectDictionary(
                ParseJsonValue(json, maximumBytes));
            if (result == null) throw new InvalidDataException("Model response is not a JSON object.");
            return result;
        }

        private static object ParseJsonValue(string json, int maximumBytes)
        {
            return CreateJsonSerializer(maximumBytes).DeserializeObject(json);
        }

        private static List<Dictionary<string, object>> ReadGatewayModels(
            Dictionary<string, object> root)
        {
            if (root == null) throw new InvalidDataException("The custom API model response is empty.");
            object dataValue;
            object[] data;
            if (!root.TryGetValue("data", out dataValue) ||
                (data = ToJsonArray(dataValue)) == null)
            {
                object modelsValue;
                if (!root.TryGetValue("models", out modelsValue) ||
                    (data = ToJsonArray(modelsValue)) == null)
                    throw new InvalidDataException("The custom API model response has no data array.");
            }
            if (data.Length > ModelCatalogMaximumCount)
                throw new InvalidDataException("The custom API returned too many models.");
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < data.Length; i++)
            {
                string id = data[i] as string;
                Dictionary<string, object> item = ToObjectDictionary(data[i]);
                if (item != null) id = GetString(item, "id") ?? GetString(item, "slug") ??
                    GetString(item, "model");
                if (IsValidModel(id) && seen.Add(id))
                {
                    if (item == null)
                    {
                        item = new Dictionary<string, object>(StringComparer.Ordinal);
                        item["id"] = id;
                    }
                    result.Add(item);
                }
            }
            return result;
        }

        private static string GetModelId(Dictionary<string, object> model)
        {
            return GetString(model, "id") ?? GetString(model, "slug") ??
                GetString(model, "model");
        }

        private static Dictionary<string, List<PiModelCandidate>>
            BuildPiModelIndex(Dictionary<string, object> root)
        {
            Dictionary<string, List<PiModelCandidate>> index =
                new Dictionary<string, List<PiModelCandidate>>(
                    StringComparer.OrdinalIgnoreCase);
            if (root == null) return index;
            foreach (KeyValuePair<string, object> providerEntry in root)
            {
                Dictionary<string, object> provider = providerEntry.Value as
                    Dictionary<string, object>;
                if (provider == null) continue;
                foreach (KeyValuePair<string, object> modelEntry in provider)
                {
                    Dictionary<string, object> metadata = ToObjectDictionary(modelEntry.Value);
                    if (metadata == null) continue;
                    string id = GetString(metadata, "id") ?? modelEntry.Key;
                    if (!IsValidModel(id)) continue;
                    List<PiModelCandidate> candidates;
                    if (!index.TryGetValue(id, out candidates))
                    {
                        candidates = new List<PiModelCandidate>();
                        index[id] = candidates;
                    }
                    candidates.Add(new PiModelCandidate {
                        Provider = providerEntry.Key,
                        Metadata = metadata
                    });
                }
            }
            foreach (KeyValuePair<string, List<PiModelCandidate>> entry in index)
                entry.Value.Sort(delegate(PiModelCandidate left, PiModelCandidate right)
                {
                    return StringComparer.OrdinalIgnoreCase.Compare(left.Provider, right.Provider);
                });
            return index;
        }

        private static Dictionary<string, object> SelectPiMetadata(string modelId,
            string gatewayBaseUrl, Dictionary<string, object> gateway,
            Dictionary<string, List<PiModelCandidate>> index)
        {
            // pi.dev providers are not consistent about the id they export:
            // google publishes "gemini-3.1-pro-preview" while openrouter
            // publishes the same model as "google/gemini-3.1-pro-preview".
            // Match over the union of the exact-id bucket and the bare-suffix
            // bucket, then filter by URL/provider evidence.
            List<PiModelCandidate> candidates = new List<PiModelCandidate>();
            string namespaceHint = null;
            List<PiModelCandidate> exact;
            if (index != null && index.TryGetValue(modelId, out exact) &&
                exact != null && exact.Count > 0)
                candidates.AddRange(exact);
            int slash = modelId.IndexOf('/');
            if (slash >= 0 && slash + 1 < modelId.Length)
            {
                namespaceHint = modelId.Substring(0, slash);
                List<PiModelCandidate> suffixed;
                if (index != null &&
                    index.TryGetValue(modelId.Substring(slash + 1), out suffixed) &&
                    suffixed != null && suffixed.Count > 0)
                    foreach (PiModelCandidate candidate in suffixed)
                        if (!ContainsPiCandidate(candidates, candidate))
                            candidates.Add(candidate);
            }
            if (candidates == null || candidates.Count == 0) return null;
            string normalizedGateway;
            TryNormalizeBaseUrl(gatewayBaseUrl, out normalizedGateway);
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidateUrl;
                if (TryNormalizeBaseUrl(GetString(candidates[i].Metadata, "baseUrl") ??
                    GetString(candidates[i].Metadata, "base_url"), out candidateUrl) &&
                    string.Equals(candidateUrl, normalizedGateway,
                        StringComparison.OrdinalIgnoreCase))
                    return candidates[i].Metadata;
            }
            string[] providerHints = new string[] {
                GetString(gateway, "provider"), GetString(gateway, "provider_id"),
                GetString(gateway, "providerId"), GetString(gateway, "owned_by"),
                namespaceHint
            };
            for (int i = 0; i < providerHints.Length; i++)
            {
                PiModelCandidate matched = FindPiProviderCandidate(candidates,
                    providerHints[i]);
                if (matched != null) return matched.Metadata;
            }
            // A slug can exist in several providers. Do not guess from the
            // model family; without an owner or URL the bundled template is
            // the only deterministic fallback.
            return candidates.Count == 1 ? candidates[0].Metadata : null;
        }

        private static bool ContainsPiCandidate(
            IList<PiModelCandidate> candidates, PiModelCandidate candidate)
        {
            if (candidates == null || candidate == null) return false;
            for (int i = 0; i < candidates.Count; i++)
            {
                PiModelCandidate existing = candidates[i];
                if (!string.Equals(existing.Provider, candidate.Provider,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(GetModelId(existing.Metadata), GetModelId(candidate.Metadata),
                        StringComparison.Ordinal)) continue;
                if (!string.Equals(GetString(existing.Metadata, "baseUrl") ??
                        GetString(existing.Metadata, "base_url"),
                        GetString(candidate.Metadata, "baseUrl") ??
                        GetString(candidate.Metadata, "base_url"),
                        StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }

        private static PiModelCandidate FindPiProviderCandidate(
            IList<PiModelCandidate> candidates, string provider)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(provider)) return null;
            provider = provider.Trim();
            PiModelCandidate match = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                string metadataProvider = GetString(candidates[i].Metadata, "provider");
                if (!string.Equals(candidates[i].Provider, provider,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(metadataProvider, provider,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) return null;
                match = candidates[i];
            }
            return match;
        }

        // OpenAI-backend-only capability fields (use_responses_lite, tool_mode,
        // comp_hash, search and node repl plumbing) must not be copied onto an
        // opencode/azure/unknown provider model that happens to share a slug.
        // Reuse the bundled template for these fields only when the exact
        // gateway or pi.dev provider carries openai/openai-codex evidence.
        private static bool ShouldUseBundledCapabilities(Dictionary<string, object> gateway,
            Dictionary<string, object> pi)
        {
            string provider = GetString(pi, "provider") ??
                GetString(gateway, "provider") ?? GetString(gateway, "provider_id") ??
                GetString(gateway, "providerId") ?? GetString(gateway, "owned_by");
            return string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "openai-codex", StringComparison.OrdinalIgnoreCase);
        }

        // pi.dev declares protocol compatibility under a nested "compat"
        // object. Read its flags between the pi.dev top-level fields and the
        // bundled template so an explicit provider "no" can override a
        // same-slug OpenAI template, while openai/openai-codex models still
        // keep template defaults when pi.dev is silent.
        private static Dictionary<string, object> GetPiCompat(
            Dictionary<string, object> pi)
        {
            if (pi == null) return null;
            return ToObjectDictionary(GetValue(pi, "compat"));
        }

        private static bool ReadCatalogBoolean(Dictionary<string, object> gateway,
            Dictionary<string, object> pi, Dictionary<string, object> bundledTemplate,
            string[] aliases, bool fallback)
        {
            bool value;
            if (TryGetBooleanAnyByKeys(gateway, out value, aliases)) return value;
            if (TryGetBooleanAnyByKeys(pi, out value, aliases)) return value;
            if (TryGetBooleanAnyByKeys(GetPiCompat(pi), out value, aliases)) return value;
            if (bundledTemplate != null &&
                TryGetBooleanAnyByKeys(bundledTemplate, out value, aliases)) return value;
            return fallback;
        }

        private static bool TryGetThinkingLevelMap(Dictionary<string, object> value,
            out Dictionary<string, object> map)
        {
            map = GetObject(value, "thinkingLevelMap");
            if (map == null) map = GetObject(value, "thinking_level_map");
            return map != null;
        }

        private static Dictionary<string, object> CreateCodexModelInfo(string modelId,
            Dictionary<string, object> gateway, Dictionary<string, object> pi,
            Dictionary<string, object> bundledTemplate, string defaultBaseInstructions,
            int priority)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(
                StringComparer.Ordinal);
            Dictionary<string, object> capabilityTemplate =
                ShouldUseBundledCapabilities(gateway, pi) ? bundledTemplate : null;
            Dictionary<string, object> capabilities = MergeModelMetadata(pi, capabilityTemplate);
            object[] bundledReasoningLevels = GetArray(capabilityTemplate,
                "supported_reasoning_levels");
            bool reasoning;
            if (!TryGetBooleanAnyByKeys(gateway, out reasoning, "reasoning",
                "supports_reasoning", "supportsReasoning"))
            {
                if (!TryGetBooleanAnyByKeys(pi, out reasoning, "reasoning",
                    "supports_reasoning", "supportsReasoning"))
                    reasoning = capabilityTemplate != null && bundledReasoningLevels != null &&
                        bundledReasoningLevels.Length > 0;
            }
            bool toolCalls;
            if (!TryGetBooleanAnyByKeys(gateway, out toolCalls, "tool_call", "tool_calls",
                "toolCall", "toolCalls", "supports_tools", "supportsTools",
                "supports_tool_calls", "supportsToolCalls"))
            {
                if (!TryGetBooleanAnyByKeys(pi, out toolCalls, "tool_call", "tool_calls",
                    "toolCall", "toolCalls", "supports_tools", "supportsTools",
                    "supports_tool_calls", "supportsToolCalls"))
                    toolCalls = capabilityTemplate == null || !string.Equals(
                        GetString(capabilityTemplate, "shell_type"), "disabled",
                        StringComparison.Ordinal);
            }
            List<string> efforts = GetReasoningEfforts(gateway, pi, capabilityTemplate,
                reasoning);
            result["slug"] = modelId;
            result["display_name"] = GetStringAny(gateway, capabilities, "display_name", "displayName",
                "name") ?? GetString(capabilityTemplate, "display_name") ?? modelId;
            result["description"] = GetStringAny(gateway, capabilities, "description") ??
                GetString(capabilityTemplate, "description") ??
                ModelDescription(gateway, capabilities, reasoning, toolCalls);
            string defaultEffort = NormalizeReasoningEffort(GetStringAny(gateway, capabilities,
                "default_reasoning_level", "defaultReasoningLevel", "default_reasoning_effort",
                "defaultReasoningEffort") ?? GetString(capabilityTemplate,
                    "default_reasoning_level"));
            if (!reasoning) defaultEffort = null;
            if (defaultEffort != null && !efforts.Contains(defaultEffort)) defaultEffort = null;
            if (defaultEffort == null && efforts.Count > 0)
                defaultEffort = ChooseDefaultReasoningEffort(efforts);
            // Product contract: the portable default model opens with the max
            // reasoning dial whenever the provider exposes it, even if the
            // gateway advertises a lower default.
            if (reasoning && string.Equals(modelId, DefaultModel,
                StringComparison.Ordinal) && efforts.Contains("max"))
                defaultEffort = "max";
            result["default_reasoning_level"] = defaultEffort;
            result["supported_reasoning_levels"] = NormalizeReasoningPresets(
                GetValueAnyWithFallback(gateway, capabilities,
                    GetValue(capabilityTemplate, "supported_reasoning_levels"),
                    "supported_reasoning_levels", "supportedReasoningLevels"), efforts);
            string shellType = NormalizeEnumAny(gateway, capabilities,
                new string[] { "unified_exec", "disabled" }, "unified_exec",
                "shell_type", "shellType");
            result["shell_type"] = toolCalls ? shellType : "disabled";
            result["visibility"] = "list";
            result["supported_in_api"] = true;
            result["priority"] = priority;
            object speedTiersValue = GetValueAny(gateway, capabilities, "additional_speed_tiers",
                "additionalSpeedTiers");
            if (speedTiersValue == null)
                speedTiersValue = GetValue(capabilityTemplate, "additional_speed_tiers");
            result["additional_speed_tiers"] = NormalizeStringArray(speedTiersValue);
            object serviceTiersValue = GetValueAny(gateway, capabilities, "service_tiers",
                "serviceTiers");
            if (serviceTiersValue == null)
                serviceTiersValue = GetValue(capabilityTemplate, "service_tiers");
            object[] serviceTiers = NormalizeServiceTiers(serviceTiersValue);
            result["service_tiers"] = serviceTiers;
            string defaultServiceTier = GetStringAny(gateway, capabilities,
                "default_service_tier", "defaultServiceTier") ??
                GetString(capabilityTemplate, "default_service_tier");
            result["default_service_tier"] = ContainsServiceTier(serviceTiers,
                defaultServiceTier) ? defaultServiceTier : null;
            result["availability_nux"] = null;
            result["upgrade"] = null;
            result["include_skills_usage_instructions"] = toolCalls &&
                ReadCatalogBoolean(gateway, pi, capabilityTemplate,
                    new string[] { "include_skills_usage_instructions",
                        "includeSkillsUsageInstructions" }, false);
            result["include_plugin_usage_instructions"] = toolCalls &&
                ReadCatalogBoolean(gateway, pi, capabilityTemplate,
                    new string[] { "include_plugin_usage_instructions",
                        "includePluginUsageInstructions" }, false);
            result["include_apps_usage_instructions"] = toolCalls &&
                ReadCatalogBoolean(gateway, pi, capabilityTemplate,
                    new string[] { "include_apps_usage_instructions",
                        "includeAppsUsageInstructions" }, false);
            bool supportsReasoningSummaryParameter = reasoning &&
                ReadCatalogBoolean(gateway, pi, capabilityTemplate,
                    new string[] { "supports_reasoning_summary_parameter",
                        "supportsReasoningSummaryParameter" }, true);
            result["supports_reasoning_summary_parameter"] =
                supportsReasoningSummaryParameter;
            result["default_reasoning_summary"] = supportsReasoningSummaryParameter ?
                NormalizeEnumAny(gateway, capabilities,
                    new string[] { "none", "auto", "concise", "detailed" }, "auto",
                    "default_reasoning_summary", "defaultReasoningSummary") :
                "none";
            bool supportsVerbosity = ReadCatalogBoolean(gateway, pi, capabilityTemplate,
                new string[] { "support_verbosity", "supportVerbosity" }, false);
            result["support_verbosity"] = supportsVerbosity;
            string verbosity = NormalizeEnumAny(gateway, capabilities,
                new string[] { "low", "medium", "high" }, null,
                "default_verbosity", "defaultVerbosity");
            result["default_verbosity"] = supportsVerbosity ? verbosity : null;
            string applyPatchType = GetStringAny(gateway, pi,
                "apply_patch_tool_type", "applyPatchToolType");
            if (applyPatchType == null) applyPatchType = GetString(capabilityTemplate,
                "apply_patch_tool_type");
            result["apply_patch_tool_type"] = toolCalls && string.Equals(applyPatchType,
                "freeform", StringComparison.OrdinalIgnoreCase) ? "freeform" : null;
            result["web_search_tool_type"] = NormalizeEnumAny(gateway, capabilities,
                new string[] { "text", "text_and_image" }, "text",
                "web_search_tool_type", "webSearchToolType");
            result["truncation_policy"] = NormalizeTruncationPolicy(GetValueAny(gateway, capabilities,
                "truncation_policy", "truncationPolicy"), capabilityTemplate);
            result["supports_image_detail_original"] =
                ReadCatalogBoolean(gateway, pi, capabilityTemplate,
                    new string[] { "supports_image_detail_original",
                        "supportsImageDetailOriginal" }, false);
            long contextWindow = GetPositiveIntegerAny(gateway, capabilities, "context_window",
                "contextWindow", "context");
            if (contextWindow <= 0) contextWindow = GetPositiveInteger(capabilityTemplate,
                "context_window");
            if (contextWindow <= 0) contextWindow = 272000;
            result["context_window"] = contextWindow > 0 ? (object)contextWindow : null;
            long maxContextWindow = GetPositiveIntegerAny(gateway, capabilities,
                "max_context_window", "maxContextWindow");
            if (maxContextWindow <= 0) maxContextWindow = GetPositiveInteger(capabilityTemplate,
                "max_context_window");
            if (maxContextWindow < contextWindow) maxContextWindow = contextWindow;
            result["max_context_window"] = maxContextWindow > 0 ? (object)maxContextWindow : null;
            long autoCompactTokenLimit = GetPositiveIntegerAny(gateway, capabilities,
                "auto_compact_token_limit", "autoCompactTokenLimit");
            if (autoCompactTokenLimit <= 0) autoCompactTokenLimit = GetPositiveInteger(
                capabilityTemplate, "auto_compact_token_limit");
            result["auto_compact_token_limit"] = autoCompactTokenLimit > 0 ?
                (object)autoCompactTokenLimit : null;
            result["comp_hash"] = NormalizeOptionalCatalogString(GetStringAny(gateway, capabilities,
                "comp_hash", "compHash") ?? GetString(capabilityTemplate, "comp_hash"), 256);
            long contextPercent = GetPositiveIntegerAny(gateway, capabilities,
                "effective_context_window_percent", "effectiveContextWindowPercent");
            if (contextPercent <= 0) contextPercent = GetPositiveInteger(capabilityTemplate,
                "effective_context_window_percent");
            if (contextPercent <= 0 || contextPercent > 100) contextPercent = 95;
            result["effective_context_window_percent"] = contextPercent;
            object toolsValue = GetValueAny(gateway, capabilities, "experimental_supported_tools",
                "experimentalSupportedTools");
            if (toolsValue == null)
                toolsValue = GetValue(capabilityTemplate, "experimental_supported_tools");
            result["experimental_supported_tools"] = toolCalls ?
                NormalizeStringArray(toolsValue) : new object[0];
            object inputValue = GetValueAny(gateway, capabilities, "input_modalities", "inputModalities",
                "input");
            if (inputValue == null) inputValue = GetValue(capabilityTemplate, "input_modalities");
            result["input_modalities"] = NormalizeInputModalities(inputValue, false);
            result["supports_search_tool"] = toolCalls && ReadCatalogBoolean(gateway, pi,
                capabilityTemplate, new string[] { "supports_search_tool",
                    "supportsSearchTool", "supportsToolSearch" }, false);
            result["use_responses_lite"] = ReadCatalogBoolean(gateway, pi,
                capabilityTemplate, new string[] { "use_responses_lite",
                    "useResponsesLite" }, false);
            result["node_repl_auto_review_required"] = toolCalls &&
                ReadCatalogBoolean(gateway, pi, capabilityTemplate,
                    new string[] { "node_repl_auto_review_required",
                        "nodeReplAutoReviewRequired" }, false);
            result["node_repl_disabled"] = !toolCalls || ReadCatalogBoolean(gateway, pi,
                capabilityTemplate, new string[] { "node_repl_disabled",
                    "nodeReplDisabled" }, false);
            string toolMode = NormalizeEnumAny(gateway, capabilities,
                new string[] { "direct", "code_mode", "code_mode_only" }, null,
                "tool_mode", "toolMode");
            result["tool_mode"] = toolCalls ? toolMode : null;
            string multiAgent = NormalizeEnumAny(gateway, capabilities,
                new string[] { "v1", "v2" }, null,
                "multi_agent_version", "multiAgentVersion");
            result["multi_agent_version"] = toolCalls ? multiAgent : null;
            string multiAgentEffort = NormalizeReasoningEffort(GetStringAny(gateway, capabilities,
                "multi_agent_reasoning_effort", "multiAgentReasoningEffort") ??
                GetString(capabilityTemplate, "multi_agent_reasoning_effort"));
            result["multi_agent_reasoning_effort"] = toolCalls && efforts.Contains(multiAgentEffort) ?
                multiAgentEffort : null;
            string autoReviewModel = GetStringAny(gateway, capabilities,
                "auto_review_model_override", "autoReviewModelOverride") ??
                GetString(capabilityTemplate, "auto_review_model_override");
            result["auto_review_model_override"] = IsValidModel(autoReviewModel) ?
                autoReviewModel : null;
            result["model_specialty"] = NormalizeOptionalCatalogString(GetStringAny(gateway, capabilities,
                "model_specialty", "modelSpecialty") ??
                GetString(capabilityTemplate, "model_specialty"), 128);
            Dictionary<string, object> messages = CloneObjectDictionary(
                GetObject(bundledTemplate, "model_messages"));
            string explicitInstructions = NormalizeCatalogText(
                GetStringAny(gateway, pi, "base_instructions", "baseInstructions"),
                512 * 1024);
            string instructions = messages == null ? null : GetString(messages,
                "instructions_template");
            if (explicitInstructions != null) instructions = explicitInstructions;
            if (string.IsNullOrEmpty(instructions))
                instructions = NormalizeCatalogText(
                    GetString(bundledTemplate, "base_instructions"), 512 * 1024);
            if (string.IsNullOrEmpty(instructions)) instructions = defaultBaseInstructions;
            if (string.IsNullOrEmpty(instructions)) instructions = DefaultDeveloperInstructions;
            if (messages == null) messages = new Dictionary<string, object>(StringComparer.Ordinal);
            messages["instructions_template"] = instructions;
            result["model_messages"] = messages;
            // Keep the legacy field as well: older Codex clients promote it
            // when a canonical template is unavailable.
            result["base_instructions"] = instructions;
            return result;
        }

        private static string ModelDescription(Dictionary<string, object> gateway,
            Dictionary<string, object> capabilities, bool reasoning, bool toolCalls)
        {
            List<string> parts = new List<string>();
            long context = GetPositiveIntegerAny(gateway, capabilities, "contextWindow",
                "context_window", "context");
            long output = GetPositiveIntegerAny(gateway, capabilities, "maxTokens",
                "max_tokens", "output_limit");
            if (context > 0) parts.Add(context.ToString("N0", CultureInfo.InvariantCulture) +
                " token context");
            if (output > 0) parts.Add(output.ToString("N0", CultureInfo.InvariantCulture) +
                " max output");
            parts.Add(reasoning ? "reasoning" : "non-reasoning");
            parts.Add(toolCalls ? "tool calling" : "no tool calling");
            if (SupportsImage(gateway) || SupportsImage(capabilities))
                parts.Add("image input");
            return string.Join("; ", parts.ToArray()) + ".";
        }

        private static object NormalizeAvailabilityNux(object raw)
        {
            Dictionary<string, object> value = ToObjectDictionary(raw);
            string message = NormalizeOptionalCatalogString(GetString(value, "message"), 4096);
            return string.IsNullOrEmpty(message) ? null :
                new Dictionary<string, object> { { "message", message } };
        }

        private static object NormalizeModelUpgrade(object raw)
        {
            Dictionary<string, object> value = ToObjectDictionary(raw);
            string model = NormalizeOptionalCatalogString(GetString(value, "model"), 512);
            string markdown = NormalizeCatalogText(
                GetString(value, "migration_markdown") ??
                GetString(value, "migrationMarkdown"), 16384);
            if (string.IsNullOrEmpty(model) || !IsValidModel(model) || markdown == null) return null;
            Dictionary<string, object> result = new Dictionary<string, object> {
                { "model", model }, { "migration_markdown", markdown }
            };
            string retirementAt = NormalizeOptionalCatalogString(
                GetString(value, "retirement_at") ?? GetString(value, "retirementAt"), 128);
            DateTime parsed;
            if (!string.IsNullOrEmpty(retirementAt) &&
                DateTime.TryParse(retirementAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
                result["retirement_at"] = parsed.ToUniversalTime().ToString("o",
                    CultureInfo.InvariantCulture);
            return result;
        }

        private static List<string> GetReasoningEfforts(Dictionary<string, object> gateway,
            Dictionary<string, object> pi, Dictionary<string, object> bundledTemplate,
            bool reasoning)
        {
            List<string> result = new List<string>();
            if (!reasoning) return result;
            string[] effortKeys = new string[] { "supported_reasoning_levels",
                "supportedReasoningLevels", "reasoning_efforts", "reasoningEfforts" };
            Dictionary<string, object> gatewayMap = null;
            bool gatewaySpecified = HasAnyKey(gateway, effortKeys) ||
                TryGetThinkingLevelMap(gateway, out gatewayMap);
            AddReasoningEfforts(result, GetValueAny(gateway, null, effortKeys));
            if (gatewayMap != null) AddReasoningMapEfforts(result, gatewayMap);
            if (gatewaySpecified) return result;
            // A provider that rejects an explicit reasoning-effort dial keeps
            // the reasoning marker but exposes no selectable levels. Its
            // thinkingLevelMap must not silently re-introduce strengths the
            // wire protocol cannot represent.
            bool supportsEffort = true;
            bool hasSupportsEffort;
            Dictionary<string, object> compat = GetPiCompat(pi);
            if (TryGetBooleanAnyByKeys(compat, out hasSupportsEffort,
                "supportsReasoningEffort", "supports_reasoning_effort"))
                supportsEffort = hasSupportsEffort;
            if (!supportsEffort) return result;
            Dictionary<string, object> piMap = null;
            bool piSpecified = HasAnyKey(pi, effortKeys) ||
                TryGetThinkingLevelMap(pi, out piMap);
            AddReasoningEfforts(result, GetValueAny(null, pi, effortKeys));
            if (piMap != null) AddReasoningMapEfforts(result, piMap);
            if (piSpecified) return result;
            if (bundledTemplate != null)
            {
                AddReasoningEfforts(result, GetValue(bundledTemplate,
                    "supported_reasoning_levels"));
                return result;
            }
            result.Add("low");
            result.Add("medium");
            result.Add("high");
            return result;
        }

        private static void AddReasoningEfforts(List<string> result, object raw)
        {
            object[] values = ToObjectArray(raw);
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                Dictionary<string, object> entry = ToObjectDictionary(values[i]);
                string effort = entry == null ? values[i] as string :
                    GetString(entry, "effort") ?? GetString(entry, "level") ??
                    GetString(entry, "reasoning_effort");
                effort = NormalizeReasoningEffort(effort);
                if (!string.IsNullOrEmpty(effort) && !result.Contains(effort)) result.Add(effort);
            }
        }

        private static void AddReasoningMapEfforts(List<string> result,
            Dictionary<string, object> map)
        {
            if (map == null) return;
            string[] order = new string[] { "none", "minimal", "low", "medium", "high",
                "xhigh", "max", "ultra", "persistent" };
            for (int i = 0; i < order.Length; i++)
            {
                object value;
                string key = order[i] == "none" ? "off" : order[i];
                if (map.TryGetValue(key, out value) && value != null &&
                    !result.Contains(order[i])) result.Add(order[i]);
            }
            foreach (KeyValuePair<string, object> entry in map)
            {
                if (entry.Value == null) continue;
                string effort = NormalizeReasoningEffort(entry.Key);
                if (!string.IsNullOrEmpty(effort) && !result.Contains(effort))
                    result.Add(effort);
            }
        }

        private static string NormalizeReasoningEffort(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string normalized = value.Trim();
            if (normalized.Length == 0 || normalized.Length > 128) return null;
            for (int i = 0; i < normalized.Length; i++)
                if (char.IsControl(normalized[i])) return null;
            string knownValue = normalized.ToLowerInvariant();
            if (knownValue == "off") return null;
            string[] allowed = new string[] { "none", "minimal", "low", "medium", "high",
                "xhigh", "max", "ultra", "persistent" };
            for (int i = 0; i < allowed.Length; i++)
                if (knownValue == allowed[i]) return knownValue;
            return normalized;
        }

        private static object[] NormalizeReasoningPresets(object raw, List<string> efforts)
        {
            object[] values = ToObjectArray(raw);
            List<object> result = new List<object>();
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    Dictionary<string, object> entry = ToObjectDictionary(values[i]);
                    string effort = entry == null ? values[i] as string :
                        GetString(entry, "effort") ?? GetString(entry, "level") ??
                        GetString(entry, "reasoning_effort");
                    effort = NormalizeReasoningEffort(effort);
                    if (string.IsNullOrEmpty(effort) || !efforts.Contains(effort)) continue;
                    string description = entry == null ? null : GetString(entry, "description");
                    bool duplicate = false;
                    for (int j = 0; j < result.Count; j++)
                    {
                        Dictionary<string, object> existing = result[j] as Dictionary<string, object>;
                        if (existing != null && string.Equals(GetString(existing, "effort"), effort,
                            StringComparison.Ordinal))
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (duplicate) continue;
                    result.Add(new Dictionary<string, object> {
                        { "effort", effort },
                        { "description", string.IsNullOrEmpty(description) ?
                            ReasoningDescription(effort) : description }
                    });
                }
            }
            for (int i = 0; i < efforts.Count; i++)
            {
                bool found = false;
                for (int j = 0; j < result.Count; j++)
                {
                    Dictionary<string, object> existing = result[j] as Dictionary<string, object>;
                    if (existing != null && string.Equals(GetString(existing, "effort"),
                        efforts[i], StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    result.Add(new Dictionary<string, object> {
                        { "effort", efforts[i] },
                        { "description", ReasoningDescription(efforts[i]) }
                    });
            }
            return result.ToArray();
        }

        private static object[] BuildReasoningPresets(List<string> efforts)
        {
            List<object> presets = new List<object>();
            for (int i = 0; i < efforts.Count; i++)
            {
                Dictionary<string, object> preset = new Dictionary<string, object>();
                preset["effort"] = efforts[i];
                preset["description"] = ReasoningDescription(efforts[i]);
                presets.Add(preset);
            }
            return presets.ToArray();
        }

        private static string ChooseDefaultReasoningEffort(List<string> efforts)
        {
            string[] preference = new string[] { "medium", "high", "low", "minimal", "none",
                "xhigh", "max", "ultra" };
            for (int i = 0; i < preference.Length; i++)
                if (efforts.Contains(preference[i])) return preference[i];
            return efforts[0];
        }

        private static string ReasoningDescription(string effort)
        {
            switch (effort)
            {
                case "none": return "No reasoning";
                case "minimal": return "Minimal reasoning";
                case "low": return "Fast responses with lighter reasoning";
                case "medium": return "Balanced speed and reasoning depth";
                case "high": return "Greater reasoning depth";
                case "xhigh": return "Extra high reasoning depth";
                case "max": return "Maximum reasoning depth";
                case "ultra": return "Maximum reasoning with task delegation";
                default: return effort;
            }
        }

        private static object[] GetInputModalities(Dictionary<string, object> pi)
        {
            return NormalizeInputModalities(GetValue(pi, "input"), false);
        }

        private static bool SupportsImage(Dictionary<string, object> pi)
        {
            object[] input = GetArray(pi, "input");
            if (input == null) input = GetArray(pi, "input_modalities");
            if (input == null) input = GetArray(pi, "inputModalities");
            if (input == null) return false;
            for (int i = 0; i < input.Length; i++)
                if (string.Equals(input[i] as string, "image", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static object GetValue(Dictionary<string, object> value, string key)
        {
            if (value == null || string.IsNullOrEmpty(key)) return null;
            object item;
            return value.TryGetValue(key, out item) ? item : null;
        }

        // An exact pi.dev match describes the selected gateway/provider. Keep
        // it ahead of the bundled same-slug template, then use that template
        // only for fields the provider metadata does not advertise. Gateway
        // fields are read separately as the explicit override layer.
        private static Dictionary<string, object> MergeModelMetadata(
            Dictionary<string, object> pi, Dictionary<string, object> bundledTemplate)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(
                StringComparer.Ordinal);
            if (pi != null)
                foreach (KeyValuePair<string, object> entry in pi)
                    if (entry.Value != null) result[entry.Key] = entry.Value;
            if (bundledTemplate != null)
                foreach (KeyValuePair<string, object> entry in bundledTemplate)
                    if (!result.ContainsKey(entry.Key) || result[entry.Key] == null)
                        if (entry.Value != null) result[entry.Key] = entry.Value;
            return result;
        }

        private static Dictionary<string, object> CloneObjectDictionary(
            Dictionary<string, object> value)
        {
            if (value == null) return null;
            Dictionary<string, object> result = new Dictionary<string, object>(
                StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in value)
                result[entry.Key] = entry.Value;
            return result;
        }

        private static bool HasAnyKey(Dictionary<string, object> value, params string[] keys)
        {
            if (value == null || keys == null) return false;
            for (int i = 0; i < keys.Length; i++)
                if (value.ContainsKey(keys[i])) return true;
            return false;
        }

        private static string GetString(Dictionary<string, object> value, string key)
        {
            object item = GetValue(value, key);
            return item as string;
        }

        private static Dictionary<string, object> GetObject(
            Dictionary<string, object> value, string key)
        {
            return ToObjectDictionary(GetValue(value, key));
        }

        private static object[] GetArray(Dictionary<string, object> value, string key)
        {
            return ToObjectArray(GetValue(value, key));
        }

        private static bool GetBoolean(Dictionary<string, object> value, string key,
            bool fallback)
        {
            object item = GetValue(value, key);
            return item is bool ? (bool)item : fallback;
        }

        private static long GetPositiveInteger(Dictionary<string, object> value, string key)
        {
            object item = GetValue(value, key);
            if (item == null) return 0;
            try
            {
                decimal number = Convert.ToDecimal(item, CultureInfo.InvariantCulture);
                if (number <= 0 || number != Decimal.Truncate(number) ||
                    number > Int64.MaxValue) return 0;
                return (long)number;
            }
            catch { return 0; }
        }

        private static object GetValueAny(Dictionary<string, object> primary,
            Dictionary<string, object> secondary, params string[] keys)
        {
            if (keys == null) return null;
            for (int i = 0; i < keys.Length; i++)
            {
                object item = GetValue(primary, keys[i]);
                if (item != null) return item;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                object item = GetValue(secondary, keys[i]);
                if (item != null) return item;
            }
            return null;
        }

        private static object GetValueAnyWithFallback(Dictionary<string, object> primary,
            Dictionary<string, object> secondary, object fallback, params string[] keys)
        {
            object value = GetValueAny(primary, secondary, keys);
            return value ?? fallback;
        }

        private static string GetStringAny(Dictionary<string, object> primary,
            Dictionary<string, object> secondary, params string[] keys)
        {
            if (keys == null) return null;
            for (int i = 0; i < keys.Length; i++)
            {
                string item = GetString(primary, keys[i]);
                if (item != null) return item;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                string item = GetString(secondary, keys[i]);
                if (item != null) return item;
            }
            return null;
        }

        private static bool TryGetBooleanAnyByKeys(Dictionary<string, object> value,
            out bool result, params string[] keys)
        {
            result = false;
            if (value == null || keys == null) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                object item = GetValue(value, keys[i]);
                if (item is bool)
                {
                    result = (bool)item;
                    return true;
                }
            }
            return false;
        }

        private static long GetPositiveIntegerAny(Dictionary<string, object> primary,
            Dictionary<string, object> secondary, params string[] keys)
        {
            if (keys != null)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    long value = GetPositiveInteger(primary, keys[i]);
                    if (value > 0) return value;
                }
                for (int i = 0; i < keys.Length; i++)
                {
                    long value = GetPositiveInteger(secondary, keys[i]);
                    if (value > 0) return value;
                }
            }
            return 0;
        }

        private static Dictionary<string, object> GetObjectAny(
            Dictionary<string, object> primary, Dictionary<string, object> secondary,
            params string[] keys)
        {
            if (keys == null) return null;
            for (int i = 0; i < keys.Length; i++)
            {
                Dictionary<string, object> item = GetObject(primary, keys[i]);
                if (item != null) return item;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                Dictionary<string, object> item = GetObject(secondary, keys[i]);
                if (item != null) return item;
            }
            return null;
        }

        private static string NormalizeOptionalCatalogString(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            value = value.Trim();
            if (value.Length == 0 || value.Length > maximumLength) return null;
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i])) return null;
            return value;
        }

        private static string NormalizeCatalogText(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            value = value.Trim();
            if (value.Length == 0 || value.Length > maximumLength) return null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n' || c == '\t') continue;
                if (char.IsControl(c)) return null;
            }
            return value;
        }

        private static Dictionary<string, object> ToObjectDictionary(object value)
        {
            Dictionary<string, object> direct = value as Dictionary<string, object>;
            if (direct != null) return direct;
            IDictionary<string, object> generic = value as IDictionary<string, object>;
            if (generic != null)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(
                    StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> entry in generic)
                    copy[entry.Key] = entry.Value;
                return copy;
            }
            IDictionary dictionary = value as IDictionary;
            if (dictionary == null) return null;
            Dictionary<string, object> result = new Dictionary<string, object>(
                StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                string key = entry.Key as string;
                if (key != null) result[key] = entry.Value;
            }
            return result.Count == 0 ? null : result;
        }

        private static object[] ToObjectArray(object value)
        {
            if (value == null) return null;
            object[] direct = value as object[];
            if (direct != null) return direct;
            if (value is string) return new object[] { value };
            IList list = value as IList;
            if (list != null)
            {
                object[] result = new object[list.Count];
                for (int i = 0; i < list.Count; i++) result[i] = list[i];
                return result;
            }
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) return null;
            ArrayList values = new ArrayList();
            foreach (object item in enumerable) values.Add(item);
            return values.ToArray();
        }

        private static object[] ToJsonArray(object value)
        {
            if (value == null || value is string || value is IDictionary) return null;
            return ToObjectArray(value);
        }

        private static object[] NormalizeStringArray(object raw)
        {
            List<object> result = new List<object>();
            object[] values = ToObjectArray(raw);
            if (values == null) return result.ToArray();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i] as string;
                if (value == null) continue;
                value = value.Trim();
                if (value.Length == 0 || value.Length > 256 ||
                    value.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0 ||
                    !seen.Add(value)) continue;
                result.Add(value);
            }
            return result.ToArray();
        }

        private static object[] NormalizeServiceTiers(object raw)
        {
            List<object> result = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            object[] values = ToObjectArray(raw);
            if (values == null) return result.ToArray();
            for (int i = 0; i < values.Length; i++)
            {
                Dictionary<string, object> entry = ToObjectDictionary(values[i]);
                string id = entry == null ? values[i] as string :
                    GetString(entry, "id") ?? GetString(entry, "slug") ??
                    GetString(entry, "value");
                if (id == null) continue;
                id = id.Trim();
                if (id.Length == 0 || id.Length > 128 || !seen.Add(id)) continue;
                string name = entry == null ? id : GetString(entry, "name");
                string description = entry == null ? null : GetString(entry, "description");
                result.Add(new Dictionary<string, object> {
                    { "id", id },
                    { "name", string.IsNullOrEmpty(name) ? id : name },
                    { "description", description ?? "" }
                });
            }
            return result.ToArray();
        }

        private static bool ContainsServiceTier(object[] serviceTiers, string id)
        {
            if (serviceTiers == null || string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < serviceTiers.Length; i++)
            {
                Dictionary<string, object> tier = serviceTiers[i] as Dictionary<string, object>;
                if (tier != null && string.Equals(GetString(tier, "id"), id,
                    StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static object[] NormalizeInputModalities(object raw, bool addImageFallback)
        {
            List<object> result = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            result.Add("text");
            seen.Add("text");
            object[] values = ToObjectArray(raw);
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    string value = values[i] as string;
                    if (value == null) continue;
                    value = value.Trim().ToLowerInvariant();
                    if ((value == "image" || value == "audio") && seen.Add(value))
                        result.Add(value);
                }
            }
            if (addImageFallback && seen.Add("image")) result.Add("image");
            return result.ToArray();
        }

        private static string NormalizeEnum(string value, string[] allowed,
            string fallback)
        {
            if (allowed != null && value != null)
            {
                string normalized = value.Trim().ToLowerInvariant();
                for (int i = 0; i < allowed.Length; i++)
                    if (string.Equals(normalized, allowed[i], StringComparison.OrdinalIgnoreCase))
                        return allowed[i];
            }
            return fallback;
        }

        private static string NormalizeEnumAny(Dictionary<string, object> primary,
            Dictionary<string, object> secondary, string[] allowed, string fallback,
            params string[] keys)
        {
            Dictionary<string, object>[] sources = new Dictionary<string, object>[] {
                primary, secondary
            };
            for (int source = 0; source < sources.Length; source++)
            {
                for (int i = 0; keys != null && i < keys.Length; i++)
                {
                    string value = GetString(sources[source], keys[i]);
                    string normalized = NormalizeEnum(value, allowed, null);
                    if (normalized != null) return normalized;
                }
            }
            return fallback;
        }

        private static object NormalizeTruncationPolicy(object raw,
            Dictionary<string, object> bundledTemplate)
        {
            object[] candidates = new object[] { raw,
                GetValue(bundledTemplate, "truncation_policy") };
            for (int i = 0; i < candidates.Length; i++)
            {
                Dictionary<string, object> entry = ToObjectDictionary(candidates[i]);
                string mode = NormalizeEnum(GetString(entry, "mode"),
                    new string[] { "bytes", "tokens" }, null);
                long limit = GetPositiveInteger(entry, "limit");
                if (mode != null && limit > 0)
                    return new Dictionary<string, object> {
                        { "mode", mode }, { "limit", limit }
                    };
            }
            return new Dictionary<string, object> {
                { "mode", "bytes" }, { "limit", 10000 }
            };
        }

        // These two settings are Codex's permission contract.  The launcher
        // supplies defaults only when config.toml has no valid root-level
        // value; once a user or Codex writes a valid value, it remains the
        // source of truth across portable starts and API saves.
        internal static bool IsValidApprovalPolicy(string value)
        {
            return string.Equals(value, "untrusted", StringComparison.Ordinal) ||
                string.Equals(value, "on-request", StringComparison.Ordinal) ||
                string.Equals(value, "never", StringComparison.Ordinal);
        }

        internal static bool IsValidSandboxMode(string value)
        {
            return string.Equals(value, "read-only", StringComparison.Ordinal) ||
                string.Equals(value, "workspace-write", StringComparison.Ordinal) ||
                string.Equals(value, "danger-full-access", StringComparison.Ordinal);
        }

        internal static bool IsValidFollowUpQueueMode(string value)
        {
            return string.Equals(value, "queue", StringComparison.Ordinal) ||
                string.Equals(value, "steer", StringComparison.Ordinal) ||
                string.Equals(value, "interrupt", StringComparison.Ordinal);
        }

        // Read only the root table.  A permission key under a project/profile
        // table is a different TOML setting and must not silently become the
        // portable default.
        internal static bool TryReadPermissionSettings(string config,
            out string approvalPolicy, out string sandboxMode)
        {
            approvalPolicy = null;
            sandboxMode = null;
            if (string.IsNullOrEmpty(config)) return false;
            bool root = true;
            bool approvalSeen = false;
            bool sandboxSeen = false;
            bool valid = true;
            using (StringReader reader = new StringReader(config))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                    if (trimmed[0] == '[')
                    {
                        root = false;
                        continue;
                    }
                    if (!root) continue;
                    int equals = trimmed.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = trimmed.Substring(0, equals).Trim();
                    if (!string.Equals(key, "approval_policy", StringComparison.Ordinal) &&
                        !string.Equals(key, "sandbox_mode", StringComparison.Ordinal)) continue;
                    string value;
                    if (!TryParseTomlString(trimmed.Substring(equals + 1).Trim(), out value))
                    {
                        valid = false;
                        continue;
                    }
                    if (string.Equals(key, "approval_policy", StringComparison.Ordinal))
                    {
                        if (approvalSeen || !IsValidApprovalPolicy(value))
                        {
                            valid = false;
                            continue;
                        }
                        approvalSeen = true;
                        approvalPolicy = value;
                    }
                    else
                    {
                        if (sandboxSeen || !IsValidSandboxMode(value))
                        {
                            valid = false;
                            continue;
                        }
                        sandboxSeen = true;
                        sandboxMode = value;
                    }
                }
            }
            return valid && approvalSeen && sandboxSeen;
        }

        // This setting is deliberately read from the exact desktop table. A
        // same-named key in another TOML table must not change the portable
        // user's follow-up behavior.
        internal static bool TryReadFollowUpQueueMode(string config, out string mode)
        {
            mode = null;
            if (string.IsNullOrEmpty(config)) return false;
            bool desktop = false;
            bool seen = false;
            bool valid = true;
            using (StringReader reader = new StringReader(config))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                    if (trimmed[0] == '[')
                    {
                        desktop = string.Equals(trimmed, "[desktop]", StringComparison.Ordinal);
                        continue;
                    }
                    if (!desktop) continue;
                    int equals = trimmed.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = trimmed.Substring(0, equals).Trim();
                    if (!string.Equals(key, "followUpQueueMode", StringComparison.Ordinal)) continue;
                    string value;
                    if (!TryParseTomlString(trimmed.Substring(equals + 1).Trim(), out value) ||
                        seen || !IsValidFollowUpQueueMode(value))
                    {
                        valid = false;
                        continue;
                    }
                    seen = true;
                    mode = value;
                }
            }
            return valid && seen;
        }

        internal static void Save(PortableLayout layout, string baseUrl, string model, string apiKey)
        {
            string normalized;
            model = (model ?? "").Trim();
            apiKey = (apiKey ?? "").Trim();
            if (!TryNormalizeBaseUrl(baseUrl, out normalized)) throw new InvalidDataException("Invalid custom API base URL.");
            if (!IsValidModel(model)) throw new InvalidDataException("Invalid custom API model.");
            if (!IsValidApiKey(apiKey)) throw new InvalidDataException("Invalid custom API key.");
            string previousBaseUrl = ReadEffectiveBaseUrl(layout);
            string previousApiKey = ReadStoredApiKey(layout);
            bool catalogOwnerChanged = !string.Equals(previousBaseUrl, normalized,
                StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previousApiKey, apiKey, StringComparison.Ordinal);
            layout.EnsureDirectories();
            if (catalogOwnerChanged)
                IOUtil.DeleteFileIfExists(layout.ModelCatalogFile);
            IOUtil.AtomicWriteText(layout.BaseUrlFile, normalized + "\r\n");
            IOUtil.AtomicWriteText(layout.ModelFile, model + "\r\n");
            IOUtil.AtomicWriteSensitiveText(layout.PlainKeyFile, apiKey + "\r\n");
            CleanupLegacyAuthentication(layout);
            WriteDeterministicConfig(layout);
        }

        internal static string ReadEffectiveBaseUrl(PortableLayout layout)
        {
            try
            {
                if (!File.Exists(layout.BaseUrlFile)) return null;
                string normalized;
                return TryNormalizeBaseUrl(File.ReadAllText(layout.BaseUrlFile, Encoding.UTF8).Trim(), out normalized) ? normalized : null;
            }
            catch { return null; }
        }

        internal static string ReadEffectiveModel(PortableLayout layout)
        {
            try
            {
                if (!File.Exists(layout.ModelFile)) return DefaultModel;
                string value = File.ReadAllText(layout.ModelFile, Encoding.UTF8).Trim();
                return IsValidModel(value) ? value : DefaultModel;
            }
            catch { return DefaultModel; }
        }

        internal static string ReadStoredApiKey(PortableLayout layout)
        {
            try
            {
                if (!File.Exists(layout.PlainKeyFile)) return null;
                string value = File.ReadAllText(layout.PlainKeyFile, Encoding.UTF8).Trim();
                return IsValidApiKey(value) ? value : null;
            }
            catch { return null; }
        }

        internal static bool TryReadRequiredConfiguration(PortableLayout layout, out string baseUrl, out string apiKey, out string model)
        {
            baseUrl = ReadEffectiveBaseUrl(layout);
            model = ReadEffectiveModel(layout);
            apiKey = ReadStoredApiKey(layout);
            return baseUrl != null && model != null && apiKey != null;
        }

        internal static bool HasCompleteApiConfiguration(PortableLayout layout)
        {
            string baseUrl;
            string apiKey;
            string model;
            bool complete = TryReadRequiredConfiguration(layout, out baseUrl, out apiKey, out model);
            apiKey = null;
            return complete;
        }

        internal static void CleanupLegacyAuthentication(PortableLayout layout)
        {
            IOUtil.DeleteFileIfExists(layout.AuthFile);
            IOUtil.DeleteFileIfExists(layout.EphemeralMarker);
            IOUtil.DeleteFileIfExists(layout.AuthBackup);
            IOUtil.DeleteFileIfExists(Path.Combine(layout.DataRoot, "data", "config", "openai-base-url.txt"));
            if (File.Exists(layout.PlainKeyFile)) IOUtil.DeleteFileIfExists(layout.VaultFile);
        }

        internal static void WriteDeterministicConfig(PortableLayout layout)
        {
            Directory.CreateDirectory(layout.CodexHome);
            string baseUrl = ReadEffectiveBaseUrl(layout) ?? UnconfiguredBaseUrl;
            string model = ReadEffectiveModel(layout);
            string reasoningEffort = ReadEffectiveReasoningEffort(layout, model);
            string approvalPolicy = DefaultApprovalPolicy;
            string sandboxMode = DefaultSandboxMode;
            string followUpQueueMode = DefaultFollowUpQueueMode;
            try
            {
                if (File.Exists(layout.ConfigFile))
                {
                    string existingConfig = File.ReadAllText(layout.ConfigFile, Encoding.UTF8);
                    string existingApprovalPolicy;
                    string existingSandboxMode;
                    // Preserve each valid permission independently so a
                    // partially written config cannot erase the other edit.
                    TryReadPermissionSettings(existingConfig, out existingApprovalPolicy,
                        out existingSandboxMode);
                    if (existingApprovalPolicy != null) approvalPolicy = existingApprovalPolicy;
                    if (existingSandboxMode != null) sandboxMode = existingSandboxMode;
                    string existingFollowUpQueueMode;
                    if (TryReadFollowUpQueueMode(existingConfig, out existingFollowUpQueueMode))
                        followUpQueueMode = existingFollowUpQueueMode;
                }
            }
            catch { }
            string[] requiredPlugins = GetRequiredPlugins(layout);
            string bundledMarketplace = Path.Combine(layout.Resources, "plugins", "openai-bundled");
            string primaryMarketplace = Path.Combine(layout.CodexHome, "offline-marketplaces", "openai-primary-runtime");
            StringBuilder text = new StringBuilder();
            text.AppendLine("# Managed by LF Portable. approval_policy and sandbox_mode remain config.toml settings.");
            text.AppendLine("model = " + QuoteToml(model));
            text.AppendLine("model_provider = " + QuoteToml(ProviderId));
            text.AppendLine(DeveloperInstructionsConfigLine);
            if (!string.IsNullOrEmpty(reasoningEffort))
                text.AppendLine("model_reasoning_effort = " + QuoteToml(reasoningEffort));
            if (File.Exists(layout.ModelCatalogFile))
                text.AppendLine("model_catalog_json = " + QuoteToml(layout.ModelCatalogFile));
            text.AppendLine("chatgpt_base_url = \"http://127.0.0.1:9\"");
            text.AppendLine("approval_policy = " + QuoteToml(approvalPolicy));
            text.AppendLine("sandbox_mode = " + QuoteToml(sandboxMode));
            text.AppendLine("check_for_update_on_startup = false");
            text.AppendLine("cli_auth_credentials_store = \"file\"");
            text.AppendLine();
            text.AppendLine("[desktop]");
            text.AppendLine("followUpQueueMode = " + QuoteToml(followUpQueueMode));
            text.AppendLine();
            text.AppendLine("[analytics]");
            text.AppendLine("enabled = false");
            text.AppendLine();
            text.AppendLine("[shell_environment_policy]");
            text.AppendLine("inherit = \"all\"");
            text.AppendLine("ignore_default_excludes = false");
            text.AppendLine("exclude = " + SecretExcludes);
            text.AppendLine("experimental_use_profile = false");
            text.AppendLine();
            text.AppendLine("[model_providers." + ProviderId + "]");
            text.AppendLine("name = \"Portable Custom Responses API\"");
            text.AppendLine("base_url = " + QuoteToml(baseUrl));
            text.AppendLine("env_key = " + QuoteToml(ApiKeyEnvironmentVariable));
            text.AppendLine("wire_api = \"responses\"");
            text.AppendLine("requires_openai_auth = false");
            text.AppendLine();
            text.AppendLine("[features]");
            text.AppendLine("plugins = true");
            text.AppendLine("remote_plugin = false");
            text.AppendLine("in_app_updates = false");
            text.AppendLine();
            text.AppendLine("[marketplaces.openai-bundled]");
            text.AppendLine("source_type = \"local\"");
            text.AppendLine("source = " + QuoteToml(bundledMarketplace));
            text.AppendLine();
            text.AppendLine("[marketplaces.openai-primary-runtime]");
            text.AppendLine("source_type = \"local\"");
            text.AppendLine("source = " + QuoteToml(primaryMarketplace));
            for (int i = 0; i < requiredPlugins.Length; i++)
            {
                text.AppendLine();
                text.AppendLine("[plugins." + QuoteToml(requiredPlugins[i]) + "]");
                text.AppendLine("enabled = true");
            }
            WriteConfigIfChanged(layout.ConfigFile, text.ToString());
        }

        private static string ReadEffectiveReasoningEffort(PortableLayout layout, string model)
        {
            string fallback = DefaultReasoningEffort;
            try
            {
                if (layout == null || !File.Exists(layout.ModelCatalogFile)) return fallback;
                FileInfo info = new FileInfo(layout.ModelCatalogFile);
                if (info.Length <= 0 || info.Length > ModelCatalogMaximumBytes) return fallback;
                Dictionary<string, object> root = ParseJsonObject(File.ReadAllText(
                    layout.ModelCatalogFile, Encoding.UTF8), ModelCatalogMaximumBytes);
                object modelsValue;
                object[] models = root.TryGetValue("models", out modelsValue) ?
                    ToObjectArray(modelsValue) : null;
                if (models == null) return fallback;
                for (int i = 0; i < models.Length; i++)
                {
                    Dictionary<string, object> entry = ToObjectDictionary(models[i]);
                    if (!string.Equals(GetString(entry, "slug"), model,
                        StringComparison.Ordinal)) continue;
                    string selected = GetString(entry, "default_reasoning_level");
                    if (!string.IsNullOrEmpty(selected)) return selected;
                    object[] levels = GetArray(entry, "supported_reasoning_levels");
                    if (levels != null)
                    {
                        for (int j = 0; j < levels.Length; j++)
                        {
                            Dictionary<string, object> level = levels[j] as Dictionary<string, object>;
                            selected = GetString(level, "effort");
                            if (!string.IsNullOrEmpty(selected)) return selected;
                        }
                    }
                    return null;
                }
            }
            catch { }
            return fallback;
        }

        internal static int CountConfiguredPlugins(string config, PortableArchitecture architecture)
        {
            string[] requiredPlugins = GetRequiredPlugins(architecture);
            int count = 0;
            for (int i = 0; i < requiredPlugins.Length; i++)
                if (config.IndexOf("[plugins.\"" + requiredPlugins[i] + "\"]", StringComparison.OrdinalIgnoreCase) >= 0) count++;
            return count;
        }

        internal static int CountConfiguredPlugins(string config, PortableLayout layout)
        {
            string[] requiredPlugins = GetRequiredPlugins(layout);
            int count = 0;
            for (int i = 0; i < requiredPlugins.Length; i++)
                if (config.IndexOf("[plugins.\"" + requiredPlugins[i] + "\"]", StringComparison.OrdinalIgnoreCase) >= 0) count++;
            return count;
        }

        internal static int RequiredPluginCount(PortableArchitecture architecture)
        {
            return GetRequiredPlugins(architecture).Length;
        }

        internal static int EnsureRequiredPluginCache(PortableLayout layout)
        {
            if (!ArchitectureInfo.HasOfficialDesktopPayload(layout.Architecture))
                throw new InvalidDataException("No official plugin cache can be repaired for architecture: " +
                    ArchitectureInfo.NameOf(layout.Architecture));
            return PluginCacheRecovery.EnsureRequiredPlugins(layout,
                GetRequiredPlugins(layout));
        }

        internal static bool RequiredPluginCacheComplete(PortableLayout layout)
        {
            if (!ArchitectureInfo.HasOfficialDesktopPayload(layout.Architecture)) return false;
            return PluginCacheRecovery.RequiredPluginCacheComplete(layout,
                GetRequiredPlugins(layout));
        }

        private static string EscapeToml(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string QuoteToml(string value)
        {
            return "\"" + EscapeToml(value) + "\"";
        }

        private static bool TryParseTomlString(string value, out string parsed)
        {
            parsed = null;
            if (string.IsNullOrEmpty(value) || value.Length < 2) return false;
            char quote = value[0];
            if (quote != '\"' && quote != '\'') return false;
            StringBuilder result = new StringBuilder();
            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (quote == '\"' && c == '\\')
                {
                    if (i + 1 >= value.Length) return false;
                    char escaped = value[++i];
                    switch (escaped)
                    {
                        case '\\': result.Append('\\'); break;
                        case '\"': result.Append('\"'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        default: return false;
                    }
                    continue;
                }
                if (c == quote)
                {
                    string suffix = value.Substring(i + 1).Trim();
                    if (suffix.Length != 0 && suffix[0] != '#') return false;
                    parsed = result.ToString();
                    return true;
                }
                if (c == '\r' || c == '\n' || c == '\0') return false;
                result.Append(c);
            }
            return false;
        }

        private static void WriteConfigIfChanged(string file, string value)
        {
            if (File.Exists(file) && string.Equals(File.ReadAllText(file, Encoding.UTF8), value, StringComparison.Ordinal)) return;
            IOUtil.AtomicWriteText(file, value);
        }
    }

    internal static class PortableEnvironment
    {
        internal const string DesktopBrandEnvironmentVariable = "CODEX_APP_BRAND";
        internal const string DesktopBrand = "codex";
        internal const string RemoteControlDisabledEnvironmentVariable = "CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED";
        internal const string DesktopUpdaterDisabledEnvironmentVariable = "CODEX_SPARKLE_ENABLED";

        internal static string FindMissingPrerequisite(PortableLayout p, bool verifyPluginCache)
        {
            string[] files = new string[] {
                Path.Combine(p.Runtime, "dependencies", "node", "bin", "node.exe"),
                Path.Combine(p.Runtime, "dependencies", "python", "python.exe"),
                Path.Combine(p.Runtime, "dependencies", "native", "git", "cmd", "git.exe"),
                Path.Combine(p.Tools, "dotnet", "dotnet.exe"),
                File.Exists(Path.Combine(p.Tools, "gh", "bin", "gh.exe")) ? Path.Combine(p.Tools, "gh", "bin", "gh.exe") : Path.Combine(p.Tools, "gh", "gh.exe"),
                Path.Combine(p.Resources, "cua_node", "bin", "node_repl.exe"),
                Path.Combine(p.Resources, "cua_node", "bin", "node_modules", "@oai", "sky", "bin", "windows", "codex-computer-use.exe"),
                Path.Combine(p.Resources, "plugins", "openai-bundled", ".agents", "plugins", "marketplace.json"),
                Path.Combine(p.CodexHome, "offline-marketplaces", "openai-primary-runtime", ".agents", "plugins", "marketplace.json")
            };
            for (int i = 0; i < files.Length; i++) if (!File.Exists(files[i])) return files[i];
            if (verifyPluginCache && !ProviderConfiguration.RequiredPluginCacheComplete(p))
                return Path.Combine(p.CodexHome, "plugins", "cache");
            return null;
        }

        internal static Dictionary<string, string> Build(PortableLayout p, string apiKey)
        {
            string runtime = p.Runtime;
            string tools = p.Tools;
            string resources = p.Resources;
            string codexExe = p.CodexExe;
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IDictionary current = Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry entry in current)
            {
                string key = entry.Key as string;
                string value = entry.Value as string;
                if (key != null && value != null && !ShouldDiscardHostVariable(key)) env[key] = value;
            }

            Set(env, "CODEX_ELECTRON_USER_DATA_PATH", p.ElectronData);
            Set(env, "CODEX_HOME", p.CodexHome);
            Set(env, "CODEX_SQLITE_HOME", p.SqliteHome);
            Set(env, "CODEX_PORTABLE_ROOT", p.Root);
            Set(env, "CODEX_CLI_PATH", codexExe);
            Set(env, DesktopBrandEnvironmentVariable, DesktopBrand);
            Set(env, RemoteControlDisabledEnvironmentVariable, "1");
            // The desktop updater is owned by LF Portable. Force the official
            // app's shared updater gate off even when the launcher inherits a
            // host environment that tries to enable it.
            Set(env, DesktopUpdaterDisabledEnvironmentVariable, "false");
            // The official desktop app appends "plugins/<marketplace>" to
            // this value. Keep it at the portable payload's resources root;
            // pointing it at resources\plugins makes the app probe
            // resources\plugins\plugins and then prune the portable cache.
            Set(env, "CODEX_ELECTRON_BUNDLED_PLUGINS_RESOURCES_PATH", resources);
            bool useHostScratch = PortableScratch.IsPrepared(p);
            string activeTemp = useHostScratch ? p.HostTemp : p.Temp;
            string activeXdgCache = useHostScratch ? p.HostXdgCache : p.XdgCache;
            string activeDotnetBundle = useHostScratch ? p.HostDotnetBundle : Path.Combine(p.Profile, "dotnet", "bundle");
            string activeNpmCache = useHostScratch ? p.HostNpmCache : Path.Combine(p.Profile, "npm");
            string activePipCache = useHostScratch ? p.HostPipCache : Path.Combine(p.Profile, "pip");
            string activeUvCache = useHostScratch ? p.HostUvCache : Path.Combine(p.Profile, "uv");
            Set(env, "HOME", p.Home);
            Set(env, "USERPROFILE", p.Home);
            Set(env, "HOMEDRIVE", Path.GetPathRoot(p.Home).TrimEnd('\\'));
            string root = Path.GetPathRoot(p.Home);
            string homePath = p.Home.Substring(root.Length - (root.EndsWith("\\", StringComparison.Ordinal) ? 1 : 0));
            Set(env, "HOMEPATH", homePath);
            Set(env, "APPDATA", p.AppData);
            Set(env, "LOCALAPPDATA", p.LocalAppData);
            Set(env, "LOCALAPPDATALOW", p.LocalAppDataLow);
            Set(env, "TEMP", activeTemp);
            Set(env, "TMP", activeTemp);
            Set(env, "TMPDIR", activeTemp);
            Set(env, "XDG_CONFIG_HOME", p.XdgConfig);
            Set(env, "XDG_CACHE_HOME", activeXdgCache);
            Set(env, "XDG_DATA_HOME", p.XdgData);
            Set(env, "XDG_STATE_HOME", p.XdgState);

            Set(env, "DOTNET_CLI_HOME", Path.Combine(p.Profile, "dotnet"));
            Set(env, "DOTNET_BUNDLE_EXTRACT_BASE_DIR", activeDotnetBundle);
            Set(env, "DOTNET_NOLOGO", "1");
            Set(env, "DOTNET_CLI_TELEMETRY_OPTOUT", "1");
            Set(env, "NUGET_PACKAGES", Path.Combine(p.Profile, "nuget"));
            Set(env, "GH_CONFIG_DIR", Path.Combine(p.Profile, "gh"));
            Set(env, "NPM_CONFIG_CACHE", activeNpmCache);
            Set(env, "npm_config_cache", activeNpmCache);
            Set(env, "PIP_CACHE_DIR", activePipCache);
            Set(env, "PYTHONUSERBASE", Path.Combine(p.Profile, "python-user"));
            // Plugins run the bundled interpreter directly from the portable
            // tree. Prevent it from materializing __pycache__ entries there;
            // old bytecode is tolerated by cache verification only as a narrow
            // compatibility allowance while this setting takes effect.
            Set(env, "PYTHONDONTWRITEBYTECODE", "1");
            Set(env, "UV_CACHE_DIR", activeUvCache);
            Set(env, "CARGO_HOME", Path.Combine(p.Profile, "cargo"));
            Set(env, "RUSTUP_HOME", Path.Combine(p.Profile, "rustup"));
            Set(env, "GIT_CONFIG_GLOBAL", Path.Combine(p.Profile, "gitconfig"));
            Set(env, "GIT_CONFIG_NOSYSTEM", "1");

            List<string> portablePath = new List<string>();
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "bin", "override"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "bin", "fallback"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "node", "bin"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "python"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "python", "Scripts"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "native", "powershell"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "native", "git", "cmd"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "native", "git", "bin"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "git", "cmd"));
            AddDirectory(portablePath, Path.Combine(runtime, "dependencies", "git", "bin"));
            AddDirectory(portablePath, Path.Combine(runtime, "node"));
            AddDirectory(portablePath, Path.Combine(runtime, "python"));
            AddDirectory(portablePath, Path.Combine(runtime, "python", "Scripts"));
            AddDirectory(portablePath, Path.Combine(runtime, "git", "cmd"));
            AddDirectory(portablePath, Path.Combine(runtime, "git", "bin"));
            AddDirectory(portablePath, Path.Combine(tools, "dotnet"));
            AddDirectory(portablePath, Path.Combine(tools, "gh", "bin"));
            AddDirectory(portablePath, Path.Combine(tools, "gh"));
            AddDirectory(portablePath, resources);
            string windowsRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrEmpty(windowsRoot))
            {
                AddDirectory(portablePath, Path.Combine(windowsRoot, "System32"));
                AddDirectory(portablePath, windowsRoot);
                AddDirectory(portablePath, Path.Combine(windowsRoot, "System32", "WindowsPowerShell", "v1.0"));
                AddDirectory(portablePath, Path.Combine(windowsRoot, "System32", "OpenSSH"));
            }

            string node = FindFile(new string[] {
                Path.Combine(runtime, "dependencies", "node", "bin", "node.exe"),
                Path.Combine(runtime, "node", "node.exe"),
                Path.Combine(resources, "cua_node", "bin", "node.exe")
            });
            if (node != null) Set(env, "CODEX_BROWSER_USE_NODE_PATH", node);
            string nodeRepl = FindFile(new string[] {
                Path.Combine(resources, "cua_node", "bin", "node_repl.exe"),
                Path.Combine(runtime, "dependencies", "node", "bin", "node_repl.exe")
            });
            if (nodeRepl != null) Set(env, "CODEX_NODE_REPL_PATH", nodeRepl);
            string git = FindFile(new string[] {
                Path.Combine(runtime, "dependencies", "native", "git", "cmd", "git.exe"),
                Path.Combine(runtime, "dependencies", "native", "git", "bin", "git.exe"),
                Path.Combine(runtime, "dependencies", "git", "cmd", "git.exe"),
                Path.Combine(runtime, "dependencies", "git", "bin", "git.exe"),
                Path.Combine(runtime, "git", "cmd", "git.exe"),
                Path.Combine(runtime, "git", "bin", "git.exe")
            });
            if (git != null) Set(env, "CODEX_PREFERRED_GIT_EXECUTABLE", git);

            string dotnet = Path.Combine(tools, "dotnet", "dotnet.exe");
            if (File.Exists(dotnet))
            {
                Set(env, "DOTNET_ROOT", Path.Combine(tools, "dotnet"));
                Set(env, "DOTNET_MULTILEVEL_LOOKUP", "0");
            }

            env["PATH"] = string.Join(";", portablePath.ToArray());
            if (!string.IsNullOrEmpty(apiKey)) env[ProviderConfiguration.ApiKeyEnvironmentVariable] = apiKey;
            else env.Remove(ProviderConfiguration.ApiKeyEnvironmentVariable);
            return env;
        }

        private static bool ShouldDiscardHostVariable(string name)
        {
            string upper = name.ToUpperInvariant();
            if (upper == "PATH" || upper == "HOME" || upper == "USERPROFILE" || upper == "HOMEDRIVE" || upper == "HOMEPATH" ||
                upper == "APPDATA" || upper == "LOCALAPPDATA" || upper == "LOCALAPPDATALOW" || upper == "TEMP" || upper == "TMP" || upper == "TMPDIR" ||
                upper == "NODE_OPTIONS" || upper == "NODE_PATH" || upper == "PYTHONHOME" || upper == "PYTHONPATH" || upper == "VIRTUAL_ENV" ||
                upper == "CONDA_PREFIX" || upper == "NPM_CONFIG_PREFIX" || upper == "DOTNET_ROOT" || upper == "ELECTRON_RUN_AS_NODE" ||
                upper == "SSH_AUTH_SOCK" || upper == "GIT_SSH" || upper == "GIT_SSH_COMMAND" || upper == "GIT_CONFIG_GLOBAL") return true;
            string[] prefixes = new string[] {
                "CODEX_", "CHATGPT_", "OPENAI_", "ANTHROPIC_", "AZURE_", "AWS_", "GOOGLE_", "GCP_",
                "GITHUB_", "GH_", "HF_", "HUGGINGFACE_", "COHERE_", "MISTRAL_", "GROQ_", "OPENROUTER_",
                "DEEPSEEK_", "ONEDRIVE", "XDG_", "CARGO_", "RUSTUP_"
            };
            for (int i = 0; i < prefixes.Length; i++) if (upper.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
            string[] suffixes = new string[] { "_API_KEY", "_TOKEN", "_SECRET", "_PASSWORD", "_CREDENTIAL", "_CREDENTIALS" };
            for (int i = 0; i < suffixes.Length; i++) if (upper.EndsWith(suffixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private static void Set(Dictionary<string, string> env, string name, string value)
        {
            if (!string.IsNullOrEmpty(value)) env[name] = value;
        }

        private static void AddDirectory(List<string> list, string path)
        {
            if (Directory.Exists(path) && !list.Contains(path)) list.Add(path);
        }

        private static string FindFile(string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++) if (File.Exists(candidates[i])) return candidates[i];
            return null;
        }
    }

    internal sealed class PackageInfo
    {
        internal string Name;
        internal string Publisher;
        internal string Architecture;
        internal Version Version;
        internal long ExpandedBytes;
        internal int FileCount;
        internal long ExecutableBytes;
    }

    internal static class PortablePackage
    {
        private const string ExpectedName = "OpenAI.Codex";
        private const string ExpectedPublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
        private const long MaximumDesktopExpandedBytes = 4L * 1024L * 1024L * 1024L;
        private const int MaximumDesktopPackageEntries = 100000;
        private const int ExtractionTimeoutMinutes = 45;
        private const int ProgressReportIntervalMilliseconds = 125;
        private static readonly uint[] Crc32Table = CreateCrc32Table();
        private static readonly FieldInfo ZipEntryCrc32Field = typeof(ZipArchiveEntry).GetField(
            "_crc32", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class ArchiveExtractionEntry
        {
            internal ZipArchiveEntry Entry;
            internal string RelativePath;
            internal string Destination;
            internal bool Directory;
        }

        private static PackageInfo ReadAndValidateManifest(string package, PortableArchitecture expectedArchitecture)
        {
            using (FileStream stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ReadAndValidateManifest(stream, expectedArchitecture);
        }

        private static PackageInfo ReadAndValidateManifest(Stream packageStream,
            PortableArchitecture expectedArchitecture)
        {
            if (packageStream == null) throw new ArgumentNullException("packageStream");
            if (!packageStream.CanRead || !packageStream.CanSeek)
                throw new ArgumentException("The MSIX manifest stream must be readable and seekable.",
                    "packageStream");
            packageStream.Position = 0;
            try
            {
                using (ZipArchive zip = new ZipArchive(packageStream, ZipArchiveMode.Read, true))
                {
                bool chatGpt = false;
                bool codex = false;
                ZipArchiveEntry manifest = null;
                HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expandedBytes = 0;
                long executableBytes = 0;
                int fileCount = 0;
                int entryCount = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (++entryCount > MaximumDesktopPackageEntries)
                        throw new InvalidDataException("Package has too many entries.");
                    bool directory = IsArchiveDirectory(entry);
                    AssertArchiveEntryAttributes(entry, directory);
                    string relative = NormalizePackageArchivePath(entry.FullName, directory);
                    if (relative.Length == 0)
                    {
                        if (directory) continue;
                        throw new InvalidDataException("Package contains an empty file path.");
                    }
                    if (!paths.Add(relative)) throw new InvalidDataException("Package contains duplicate paths.");
                    if (!directory)
                    {
                        if (entry.Length < 0 || entry.Length > MaximumDesktopExpandedBytes - expandedBytes)
                            throw new InvalidDataException("Package expands beyond its limit.");
                        expandedBytes += entry.Length;
                        fileCount++;
                    }
                    if (string.Equals(relative, "AppxManifest.xml", StringComparison.OrdinalIgnoreCase)) manifest = entry;
                    if (!directory && (string.Equals(relative, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(relative, "app/ChatGPT.exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (chatGpt) throw new InvalidDataException("Package contains multiple desktop executables.");
                        chatGpt = true;
                        executableBytes = entry.Length;
                    }
                    if (string.Equals(relative, "resources/codex.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(relative, "app/resources/codex.exe", StringComparison.OrdinalIgnoreCase)) codex = true;
                }
                if (manifest == null || manifest.Length <= 0 || manifest.Length > 2 * 1024 * 1024) throw new InvalidDataException("Manifest is missing or invalid.");
                if (!chatGpt || executableBytes <= 0 || !codex || fileCount == 0 || expandedBytes == 0)
                    throw new InvalidDataException("Required application files are missing.");
                using (Stream manifestStream = manifest.Open())
                {
                    PackageInfo result = ParseAndValidateManifest(manifestStream, expectedArchitecture);
                    result.ExpandedBytes = expandedBytes;
                    result.FileCount = fileCount;
                    result.ExecutableBytes = executableBytes;
                    return result;
                }
            }
            }
            finally { packageStream.Position = 0; }
        }

        private static PackageInfo ParseAndValidateManifest(Stream stream, PortableArchitecture expectedArchitecture)
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.MaxCharactersInDocument = 2 * 1024 * 1024;
            XmlDocument document = new XmlDocument();
            document.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(stream, settings)) document.Load(reader);
            XmlElement identity = document.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']") as XmlElement;
            if (identity == null) throw new InvalidDataException("Package identity is missing.");
            PackageInfo info = new PackageInfo();
            info.Name = identity.GetAttribute("Name");
            info.Publisher = identity.GetAttribute("Publisher");
            info.Architecture = identity.GetAttribute("ProcessorArchitecture");
            Version version;
            if (!Version.TryParse(identity.GetAttribute("Version"), out version)) throw new InvalidDataException("Package version is invalid.");
            info.Version = version;
            if (!string.Equals(info.Name, ExpectedName, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected package identity.");
            if (!string.Equals(info.Publisher, ExpectedPublisher, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected package publisher.");
            string expectedPackageArchitecture = ArchitectureInfo.NameOf(expectedArchitecture);
            if (!string.Equals(info.Architecture, expectedPackageArchitecture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package architecture does not match the current Windows architecture.");
            XmlElement display = document.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='PublisherDisplayName']") as XmlElement;
            if (display == null || !string.Equals(display.InnerText.Trim(), "OpenAI", StringComparison.Ordinal))
                throw new InvalidDataException("Publisher display name is invalid.");
            return info;
        }

        internal static void ExtractZipArchive(string package, string staging,
            long expectedBytes, int expectedFiles, long maximumBytes, int maximumEntries,
            Action<long, long, int, int> progress,
            Func<ZipArchiveEntry, bool, string> resolvePath)
        {
            if (!File.Exists(package)) throw new FileNotFoundException("Package is missing.", package);
            using (FileStream source = new FileStream(package, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                ExtractZipArchive(source, staging, expectedBytes, expectedFiles, maximumBytes,
                    maximumEntries, progress, resolvePath);
        }

        internal static void ExtractZipArchive(Stream source, string staging,
            long expectedBytes, int expectedFiles, long maximumBytes, int maximumEntries,
            Action<long, long, int, int> progress,
            Func<ZipArchiveEntry, bool, string> resolvePath)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!source.CanRead || !source.CanSeek)
                throw new ArgumentException("The package stream must be readable and seekable.", "source");
            if (string.IsNullOrEmpty(staging) || !Directory.Exists(staging))
                throw new DirectoryNotFoundException("Package staging directory is missing.");
            if (maximumBytes <= 0 || maximumEntries <= 0 || resolvePath == null)
                throw new ArgumentOutOfRangeException("Package extraction limits are invalid.");
            if (Directory.GetFileSystemEntries(staging, "*", SearchOption.TopDirectoryOnly).Length != 0)
                throw new IOException("Package staging directory is not empty.");

            string root = Path.GetFullPath(staging).TrimEnd('\\');
            List<ArchiveExtractionEntry> plan = new List<ArchiveExtractionEntry>();
            HashSet<string> destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            int totalFiles = 0;
            Stopwatch deadline = Stopwatch.StartNew();
            source.Position = 0;
            try
            {
                using (ZipArchive archive = new ZipArchive(source, ZipArchiveMode.Read, true))
                {
                int entryCount = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (++entryCount > maximumEntries)
                        throw new InvalidDataException("Package contains too many archive entries.");
                    bool directory = IsArchiveDirectory(entry);
                    AssertArchiveEntryAttributes(entry, directory);
                    string relative = resolvePath(entry, directory);
                    if (relative == null) continue;
                    if (string.IsNullOrEmpty(relative))
                    {
                        if (directory) continue;
                        throw new InvalidDataException("Package contains an empty file path.");
                    }
                    ValidateResolvedArchivePath(relative, directory);
                    string destination = ResolveArchiveDestination(root, relative);
                    if (!destinations.Add(destination))
                        throw new InvalidDataException("Package contains duplicate output paths.");
                    if (!directory)
                    {
                        if (entry.Length < 0 || entry.Length > maximumBytes - totalBytes)
                            throw new InvalidDataException("Package expands beyond its limit.");
                        totalBytes += entry.Length;
                        totalFiles++;
                    }
                    plan.Add(new ArchiveExtractionEntry {
                        Entry = entry,
                        RelativePath = relative,
                        Destination = destination,
                        Directory = directory
                    });
                }
                if (totalFiles <= 0 || totalBytes <= 0)
                    throw new InvalidDataException("Package contains no file content.");
                if (expectedBytes >= 0 && totalBytes != expectedBytes)
                    throw new InvalidDataException("Package expanded byte count changed after validation.");
                if (expectedFiles >= 0 && totalFiles != expectedFiles)
                    throw new InvalidDataException("Package file count changed after validation.");

                if (progress != null) progress(0, totalBytes, 0, totalFiles);
                byte[] buffer = new byte[1024 * 1024];
                long completedBytes = 0;
                int completedFiles = 0;
                Stopwatch reporter = Stopwatch.StartNew();
                HashSet<string> preparedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                preparedDirectories.Add(root);
                try
                {
                    for (int i = 0; i < plan.Count; i++)
                    {
                        if (deadline.Elapsed.TotalMinutes >= ExtractionTimeoutMinutes)
                            throw new TimeoutException("Package extraction timed out.");
                        ArchiveExtractionEntry item = plan[i];
                        if (item.Directory)
                        {
                            if (preparedDirectories.Add(item.Destination))
                            {
                                EnsureArchiveDirectory(item.Destination);
                                AssertNoReparseAncestry(item.Destination, root);
                            }
                            continue;
                        }

                        string directory = GetPathParentLongSafe(item.Destination);
                        if (string.IsNullOrEmpty(directory))
                            throw new InvalidDataException("Package output file has no parent directory.");
                        if (preparedDirectories.Add(directory))
                        {
                            AssertNoReparseAncestry(directory, root);
                            EnsureArchiveDirectory(directory);
                            AssertNoReparseAncestry(directory, root);
                        }
                        long written = 0;
                        uint crc = 0xFFFFFFFFU;
                        using (Stream input = item.Entry.Open())
                        using (FileStream output = OpenArchiveOutput(item.Destination, buffer.Length))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                if (deadline.Elapsed.TotalMinutes >= ExtractionTimeoutMinutes)
                                    throw new TimeoutException("Package extraction timed out.");
                                written += read;
                                if (written > item.Entry.Length || completedBytes > totalBytes - read)
                                    throw new InvalidDataException("Package entry expanded beyond its declared length.");
                                output.Write(buffer, 0, read);
                                crc = UpdateCrc32(crc, buffer, 0, read);
                                completedBytes += read;
                                if (reporter.ElapsedMilliseconds >= ProgressReportIntervalMilliseconds)
                                {
                                    if (progress != null) progress(completedBytes, totalBytes,
                                        completedFiles, totalFiles);
                                    reporter.Restart();
                                }
                            }
                            output.Flush();
                            SetArchiveLastWriteTime(output.SafeFileHandle,
                                item.Entry.LastWriteTime.UtcDateTime);
                        }
                        if (written != item.Entry.Length || ~crc != ReadArchiveEntryCrc32(item.Entry))
                            throw new InvalidDataException("Package entry integrity check failed: " +
                                item.RelativePath);
                        completedFiles++;
                        if (reporter.ElapsedMilliseconds >= ProgressReportIntervalMilliseconds ||
                            completedFiles == totalFiles)
                        {
                            if (progress != null) progress(completedBytes, totalBytes,
                                completedFiles, totalFiles);
                            reporter.Restart();
                        }
                    }
                }
                finally { Array.Clear(buffer, 0, buffer.Length); }
                if (completedBytes != totalBytes || completedFiles != totalFiles)
                    throw new InvalidDataException("Package extraction was incomplete.");
                }
            }
            finally { source.Position = 0; }
        }

        private static bool IsArchiveDirectory(ZipArchiveEntry entry)
        {
            return entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(entry.Name);
        }

        private static void AssertArchiveEntryAttributes(ZipArchiveEntry entry, bool directory)
        {
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            uint unixType = (attributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (attributes & 0x400) != 0)
                throw new InvalidDataException("Package contains a link or reparse-point entry.");
            if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                throw new InvalidDataException("Package contains an unsupported archive entry type.");
            if (directory && unixType == 0x8000)
                throw new InvalidDataException("Package directory entry has a file type.");
            if (!directory && unixType == 0x4000)
                throw new InvalidDataException("Package file entry has a directory type.");
        }

        private static string ResolveArchiveDestination(string root, string relative)
        {
            string normalizedRoot = GetFullPathLongSafe(root).TrimEnd('\\');
            string destination = normalizedRoot + "\\" +
                relative.Replace('/', Path.DirectorySeparatorChar);
            if (string.Equals(destination, root, StringComparison.OrdinalIgnoreCase) ||
                !IsPathWithin(destination, normalizedRoot))
                throw new InvalidDataException("Package output path is outside its staging directory.");
            return destination;
        }

        private static string NormalizePackageArchivePath(string path, bool directory)
        {
            string normalized = PortableBundle.NormalizeArchivePath(path);
            if (normalized.Length == 0) return normalized;
            string[] segments = normalized.Split('/');
            StringBuilder result = new StringBuilder(normalized.Length);
            for (int i = 0; i < segments.Length; i++)
            {
                string decoded = segments[i].IndexOf('%') < 0 ? segments[i] :
                    Uri.UnescapeDataString(segments[i]);
                AssertArchivePathSegment(decoded, directory || i < segments.Length - 1);
                if (i != 0) result.Append('/');
                result.Append(decoded);
            }
            return result.ToString();
        }

        private static void ValidateResolvedArchivePath(string relative, bool directory)
        {
            if (relative.StartsWith("/", StringComparison.Ordinal) ||
                relative.IndexOf('\\') >= 0 || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("Package contains an unsafe output path.");
            string[] segments = relative.Split('/');
            for (int i = 0; i < segments.Length; i++)
                AssertArchivePathSegment(segments[i], directory || i < segments.Length - 1);
        }

        private static void AssertArchivePathSegment(string segment, bool directory)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." ||
                segment.Length > 255 || segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsReservedWindowsName(segment))
                throw new InvalidDataException("Package contains an unsafe path segment.");
            if (directory)
                for (int i = 0; i < segment.Length; i++) if (segment[i] > 127)
                    throw new InvalidDataException("Package contains a non-ASCII directory name.");
        }

        private static void EnsureArchiveDirectory(string path)
        {
            string extended = ToExtendedPath(path);
            try
            {
                Directory.CreateDirectory(extended);
                return;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }

            if (ArchiveDirectoryExists(path)) return;
            string parent = GetPathParentLongSafe(path);
            if (!string.IsNullOrEmpty(parent) && !ArchiveDirectoryExists(parent))
                EnsureArchiveDirectory(parent);
            if (!NativeMethods.CreateDirectory(extended, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 183 && !ArchiveDirectoryExists(path))
                    throw new Win32Exception(error, "Long-path directory creation failed: " + path);
            }
            if (!ArchiveDirectoryExists(path))
                throw new IOException("Long-path directory creation could not be verified: " + path);
        }

        private static bool ArchiveDirectoryExists(string path)
        {
            uint attributes = NativeMethods.GetFileAttributes(ToExtendedPath(path));
            return attributes != NativeMethods.InvalidFileAttributes &&
                (attributes & (uint)FileAttributes.Directory) != 0;
        }

        private static FileStream OpenArchiveOutput(string path, int bufferSize)
        {
            IntPtr raw = NativeMethods.CreateFile(ToExtendedPath(path), NativeMethods.GenericWrite,
                0, IntPtr.Zero, NativeMethods.CreateNew,
                NativeMethods.FileAttributeNormal | NativeMethods.FileFlagSequentialScan, IntPtr.Zero);
            if (raw == NativeMethods.InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW failed: " + path);
            SafeFileHandle handle = new SafeFileHandle(raw, true);
            try { return new FileStream(handle, FileAccess.Write, bufferSize, false); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void SetArchiveLastWriteTime(SafeFileHandle handle, DateTime utc)
        {
            long value = utc.ToFileTimeUtc();
            System.Runtime.InteropServices.ComTypes.FILETIME timestamp =
                new System.Runtime.InteropServices.ComTypes.FILETIME();
            timestamp.dwLowDateTime = unchecked((int)(value & 0xFFFFFFFFL));
            timestamp.dwHighDateTime = unchecked((int)(value >> 32));
            if (!NativeMethods.SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref timestamp))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Package entry timestamp could not be restored.");
        }

        private static uint[] CreateCrc32Table()
        {
            uint[] table = new uint[256];
            for (int index = 0; index < table.Length; index++)
            {
                uint value = unchecked((uint)index);
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1U) != 0 ? (value >> 1) ^ 0xEDB88320U : value >> 1;
                table[index] = value;
            }
            return table;
        }

        private static uint ReadArchiveEntryCrc32(ZipArchiveEntry entry)
        {
            if (ZipEntryCrc32Field == null)
                throw new PlatformNotSupportedException("The .NET ZIP implementation does not expose entry CRC metadata.");
            object value = ZipEntryCrc32Field.GetValue(entry);
            if (!(value is uint)) throw new InvalidDataException("Package entry CRC metadata is invalid.");
            return (uint)value;
        }

        private static uint UpdateCrc32(uint crc, byte[] buffer, int offset, int count)
        {
            int end = checked(offset + count);
            for (int i = offset; i < end; i++) crc = (crc >> 8) ^
                Crc32Table[(int)((crc ^ buffer[i]) & 0xFFU)];
            return crc;
        }

        private static void ValidateExtracted(string staging, PackageInfo expected)
        {
            string exe = Path.Combine(staging, "ChatGPT.exe");
            string codex = Path.Combine(staging, "resources", "codex.exe");
            if (!File.Exists(exe) || new FileInfo(exe).Length < 100000) throw new InvalidDataException("Extracted ChatGPT.exe is invalid.");
            if (!File.Exists(codex) || new FileInfo(codex).Length < 100000) throw new InvalidDataException("Extracted codex.exe is invalid.");
            PortableArchitecture expectedArchitecture = ArchitectureInfo.ParseName(expected.Architecture);
            if (expectedArchitecture == PortableArchitecture.Unknown)
                throw new InvalidDataException("Extracted package has an unsupported architecture value.");
            if (!ArchitectureInfo.IsMachineCompatible(exe, expectedArchitecture) ||
                !ArchitectureInfo.IsMachineCompatible(codex, expectedArchitecture))
                throw new InvalidDataException("Extracted desktop payload machine architecture is inconsistent with its package manifest.");
            // The manifest stays one level above payload for the current official MSIX layout.
            string manifestPath = Path.Combine(staging, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) manifestPath = Path.Combine(Path.GetDirectoryName(staging), "AppxManifest.xml");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Extracted package manifest is missing.");
            PackageInfo actual;
            using (FileStream stream = File.OpenRead(manifestPath))
                actual = ParseAndValidateManifest(stream, expectedArchitecture);
            if (!actual.Version.Equals(expected.Version)) throw new InvalidDataException("Extracted manifest version changed.");
            ValidateOfficialBundledPlugins(staging);
        }

        private static void ValidateOfficialBundledPlugins(string payloadRoot)
        {
            string pluginsRoot = Path.Combine(payloadRoot, "resources", "plugins",
                "openai-bundled", "plugins");
            ProviderConfiguration.DiscoverBundledPluginNames(pluginsRoot);
        }

        // Package activation applies the same signature, manifest, archive-entry,
        // extraction, payload, and LF-branding postconditions on every install.
        internal static PackageInfo ExtractPreparedDesktopPayload(string package,
            string staging, PortableArchitecture expectedArchitecture,
            PackageInfo expected, Action<long, long, int, int> progress,
            Action verifyingAndBranding, out string payloadRoot)
        {
            if (!File.Exists(package)) throw new FileNotFoundException("MSIX not found.", package);
            using (FileStream packageStream = new FileStream(package, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                return ExtractPreparedDesktopPayload(package, packageStream, staging,
                    expectedArchitecture, expected, progress, verifyingAndBranding, out payloadRoot);
        }

        internal static PackageInfo ExtractPreparedDesktopPayload(string package,
            FileStream packageStream, string staging, PortableArchitecture expectedArchitecture,
            PackageInfo expected, Action<long, long, int, int> progress,
            Action verifyingAndBranding, out string payloadRoot)
        {
            if (packageStream == null) throw new ArgumentNullException("packageStream");
            if (!packageStream.CanRead || !packageStream.CanSeek)
                throw new ArgumentException("The MSIX stream must be readable and seekable.",
                    "packageStream");
            if (string.IsNullOrEmpty(staging))
                throw new ArgumentException("Desktop package staging is blank.", "staging");
            Directory.CreateDirectory(staging);
            if (Directory.GetFileSystemEntries(staging, "*", SearchOption.TopDirectoryOnly).Length != 0)
                throw new IOException("Desktop package staging directory is not empty.");
            payloadRoot = null;
            if (!SignatureVerifier.Verify(package, packageStream))
                throw new InvalidDataException("The MSIX signature is not trusted.");
            PackageInfo info = ReadAndValidateManifest(packageStream, expectedArchitecture);
            if (expected != null && !PackageInfoEquals(expected, info))
                throw new InvalidDataException("The desktop package changed after verification.");
            ExtractZipArchive(packageStream, staging, info.ExpandedBytes, info.FileCount,
                MaximumDesktopExpandedBytes, MaximumDesktopPackageEntries, progress,
                delegate(ZipArchiveEntry entry, bool directory)
                {
                    return NormalizePackageArchivePath(entry.FullName, directory);
                });
            payloadRoot = GetPayloadRoot(staging);
            if (verifyingAndBranding != null) verifyingAndBranding();
            ValidateExtracted(payloadRoot, info);
            PortableBranding.PreparePayload(payloadRoot);
            if (!PortableBranding.IsPrepared(payloadRoot))
                throw new InvalidDataException("The MSIX did not produce a prepared LF payload.");
            return info;
        }

        private static bool PackageInfoEquals(PackageInfo expected, PackageInfo actual)
        {
            return expected != null && actual != null &&
                string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) &&
                string.Equals(expected.Publisher, actual.Publisher, StringComparison.Ordinal) &&
                string.Equals(expected.Architecture, actual.Architecture, StringComparison.OrdinalIgnoreCase) &&
                expected.Version.Equals(actual.Version) &&
                expected.ExpandedBytes == actual.ExpandedBytes &&
                expected.FileCount == actual.FileCount &&
                expected.ExecutableBytes == actual.ExecutableBytes;
        }

        private static string GetPayloadRoot(string staging)
        {
            string nested = Path.Combine(staging, "app");
            if (File.Exists(Path.Combine(nested, "ChatGPT.exe"))) return nested;
            return staging;
        }

        internal static void AssertExtractedTreeNoReparse(string root)
        {
            List<string> files = new List<string>();
            List<string> directories = new List<string>();
            CollectPackageTree(root, files, directories);
        }

        internal static void AssertExtractedTreeNoReparse(string root, long expectedBytes,
            int expectedFiles)
        {
            List<string> files = new List<string>();
            List<string> directories = new List<string>();
            List<long> fileLengths = new List<long>();
            CollectPackageTree(root, files, directories, fileLengths);
            if (files.Count != expectedFiles)
                throw new InvalidDataException("Extracted package file count differs from its archive.");
            long totalBytes = 0;
            for (int i = 0; i < fileLengths.Count; i++)
            {
                long length = fileLengths[i];
                if (length < 0 || length > expectedBytes - totalBytes)
                    throw new InvalidDataException("Extracted package size exceeds its archive contract.");
                totalBytes += length;
            }
            if (totalBytes != expectedBytes)
                throw new InvalidDataException("Extracted package size differs from its archive.");
        }

        private static void CollectPackageTree(string current, List<string> files, List<string> directories)
        {
            CollectPackageTree(current, files, directories, null);
        }

        private static void CollectPackageTree(string current, List<string> files,
            List<string> directories, List<long> fileLengths)
        {
            string nativeCurrent = ToExtendedPath(current);
            NativeMethods.WIN32_FIND_DATA data;
            IntPtr find = NativeMethods.FindFirstFile(nativeCurrent.TrimEnd('\\') + "\\*", out data);
            if (find == NativeMethods.InvalidHandleValue)
            {
                int firstError = Marshal.GetLastWin32Error();
                if (firstError == 2 || firstError == 3) return;
                throw new Win32Exception(firstError, "Long-path package enumeration failed: " + current);
            }
            try
            {
                bool more = true;
                while (more)
                {
                    string name = data.cFileName;
                    if (name != "." && name != "..")
                    {
                        string child = nativeCurrent.TrimEnd('\\') + "\\" + name;
                        FileAttributes attributes = data.dwFileAttributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Reparse points are not allowed in an extracted package.");
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Add(child);
                            CollectPackageTree(child, files, directories, fileLengths);
                        }
                        else
                        {
                            files.Add(child);
                            if (fileLengths != null)
                                fileLengths.Add(((long)data.nFileSizeHigh << 32) | data.nFileSizeLow);
                        }
                    }
                    more = NativeMethods.FindNextFile(find, out data);
                    if (!more)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 18)
                            throw new Win32Exception(error, "Long-path package enumeration failed: " + current);
                    }
                }
            }
            finally { NativeMethods.FindClose(find); }
        }

        private static string ToExtendedPath(string path)
        {
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
            string full = GetFullPathLongSafe(path).Replace('/', '\\');
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }

        private static bool IsReservedWindowsName(string value)
        {
            string stem = value;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            stem = stem.ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                stem == "CLOCK$") return true;
            return stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.Ordinal) ||
                 stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] >= '1' && stem[3] <= '9';
        }

        private static bool IsRegularFile(string path)
        {
            try
            {
                return File.Exists(path) &&
                    (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
            }
            catch { return false; }
        }

        private static void AssertNoReparseAncestry(string path, string root)
        {
            string current = GetFullPathLongSafe(path).TrimEnd('\\');
            string boundary = GetFullPathLongSafe(root).TrimEnd('\\');
            if (!IsPathWithin(current, boundary))
                throw new InvalidDataException("Package path is outside its protected root.");
            while (true)
            {
                uint attributes = NativeMethods.GetFileAttributes(ToExtendedPath(current));
                if (attributes != NativeMethods.InvalidFileAttributes &&
                    (attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package path contains a reparse point.");
                if (string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase)) return;
                string parent = GetPathParentLongSafe(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package path hierarchy is invalid.");
                current = parent.TrimEnd('\\');
            }
        }

        private static bool IsPathWithin(string candidate, string root)
        {
            string fullCandidate = GetFullPathLongSafe(candidate).TrimEnd('\\');
            string fullRoot = GetFullPathLongSafe(root).TrimEnd('\\');
            return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                fullCandidate.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetFullPathLongSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is empty.", "path");
            string normalized = path.Replace('/', '\\');
            if (normalized.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                normalized = "\\\\" + normalized.Substring(8);
            else if (normalized.StartsWith("\\\\?\\", StringComparison.Ordinal))
                normalized = normalized.Substring(4);
            bool driveAbsolute = normalized.Length >= 3 && normalized[1] == ':' && normalized[2] == '\\';
            bool uncAbsolute = normalized.StartsWith("\\\\", StringComparison.Ordinal);
            if (normalized.Length < 240 || (!driveAbsolute && !uncAbsolute))
                return Path.GetFullPath(normalized);
            string[] segments = normalized.Split('\\');
            for (int i = 0; i < segments.Length; i++)
                if (segments[i] == "." || segments[i] == "..")
                    throw new InvalidDataException("Long package path contains a traversal segment.");
            return normalized;
        }

        private static string GetPathParentLongSafe(string path)
        {
            string normalized = GetFullPathLongSafe(path).TrimEnd('\\');
            if (normalized.Length < 240) return Path.GetDirectoryName(normalized);
            int separator = normalized.LastIndexOf('\\');
            if (separator < 0) return null;
            if (separator == 2 && normalized.Length >= 3 && normalized[1] == ':')
                return normalized.Substring(0, 3);
            if (separator == 1 && normalized.StartsWith("\\\\", StringComparison.Ordinal)) return null;
            return normalized.Substring(0, separator);
        }

        internal static PackageInfo StageVerifiedReleasePayload(PortableLayout layout, string package,
            PortableArchitecture expectedArchitecture)
        {
            return StageVerifiedReleasePayload(layout, package, expectedArchitecture, null);
        }

        internal static PackageInfo StageVerifiedReleasePayload(PortableLayout layout, string package,
            PortableArchitecture expectedArchitecture, Action<FirstLaunchProgress> progress)
        {
            if (!File.Exists(package)) throw new FileNotFoundException("MSIX not found.", package);
            layout.EnsureDirectories();
            string transaction = Path.Combine(layout.Updates, "release-" +
                Guid.NewGuid().ToString("N").Substring(0, 10));
            string staging = Path.Combine(transaction, "stage");
            string backup = Path.Combine(transaction, "previous");
            string failed = Path.Combine(transaction, "failed");
            string destination = expectedArchitecture == PortableArchitecture.X64 ?
                Path.Combine(layout.DataRoot, "app", "current") :
                Path.Combine(layout.Tools, "desktop-payloads", "arm64", "current");
            bool existingMoved = false;
            bool existingWasDirectory = false;
            bool newActivated = false;
            bool retainTransaction = false;
            try
            {
                // The transaction name is generated locally, but a pre-existing
                // reparse point must never turn extraction or cleanup into an
                // operation outside CodexData\updates.
                AssertNoReparseAncestry(transaction, layout.Updates);
                FileAttributes transactionAttributes;
                if (TryGetExistingPathAttributes(transaction, out transactionAttributes))
                {
                    if ((transactionAttributes & FileAttributes.Directory) == 0)
                        throw new IOException("Release payload transaction is not a directory: " + transaction);
                }
                else
                {
                    Directory.CreateDirectory(transaction);
                }
                AssertNoReparseAncestry(transaction, layout.Updates);
                FileAttributes stagingAttributes;
                if (TryGetExistingPathAttributes(staging, out stagingAttributes) &&
                    (stagingAttributes & FileAttributes.Directory) == 0)
                    throw new IOException("Release payload staging is not a directory: " + staging);
                AssertNoReparseAncestry(staging, layout.Updates);
                if (progress != null) progress(new FirstLaunchProgress(FirstLaunchPreparationStage.ValidatingDesktopPackage));
                string payload;
                PackageInfo info = ExtractPreparedDesktopPayload(package, staging,
                    expectedArchitecture, null,
                    delegate(long completedBytes, long totalBytes, int completedFiles, int totalFiles)
                    {
                        if (progress != null) progress(new FirstLaunchProgress(
                            FirstLaunchPreparationStage.ExtractingDesktopPackage,
                            completedBytes, totalBytes, completedFiles, totalFiles));
                    }, delegate
                    {
                        if (progress != null) progress(new FirstLaunchProgress(
                            FirstLaunchPreparationStage.VerifyingAndBrandingDesktop));
                    }, out payload);
                // PreparePayload includes a complete ASAR postcondition after its
                // mutations. No branded file changes between that check and the
                // atomic directory activation below.
                string parent = Path.GetDirectoryName(destination);
                if (string.IsNullOrEmpty(parent)) throw new InvalidDataException("Release payload destination has no parent.");
                AssertNoReparseAncestry(parent, layout.DataRoot);
                FileAttributes existingAttributes;
                if (TryGetExistingPathAttributes(destination, out existingAttributes))
                {
                    AssertNoReparseAncestry(destination, layout.DataRoot);
                    existingWasDirectory = (existingAttributes & FileAttributes.Directory) != 0;
                    if (!existingWasDirectory && !IsRegularFile(destination))
                        throw new IOException("Release payload destination is not a regular package path: " +
                            destination);
                    if (existingWasDirectory) AssertExtractedTreeNoReparse(destination);
                    MoveReleasePayloadPath(destination, backup, existingWasDirectory);
                    existingMoved = true;
                }
                MoveReleasePayloadPath(payload, destination, true);
                newActivated = true;
                // MoveFileW is a same-volume atomic rename. The source was fully
                // verified immediately before activation, so re-reading both large
                // EXEs and app.asar at the destination cannot add assurance here.
                // Verify only that the activated tree contains its required anchor.
                if (!File.Exists(Path.Combine(destination, "ChatGPT.exe")) ||
                    !File.Exists(Path.Combine(destination, PortableBranding.DesktopExecutableName)))
                    throw new InvalidDataException("The installed desktop payload is incomplete.");
                if (progress != null) progress(new FirstLaunchProgress(FirstLaunchPreparationStage.DesktopPayloadReady));
                return info;
            }
            catch (Exception activationError)
            {
                List<Exception> rollbackErrors = new List<Exception>();
                if (newActivated)
                {
                    try { MoveReleasePayloadPath(destination, failed, true); }
                    catch (Exception ex) { rollbackErrors.Add(ex); }
                }
                if (existingMoved)
                {
                    try
                    {
                        FileAttributes occupiedAttributes;
                        if (TryGetExistingPathAttributes(destination, out occupiedAttributes))
                            throw new IOException("Release payload rollback destination is occupied: " +
                                destination);
                        MoveReleasePayloadPath(backup, destination, existingWasDirectory);
                    }
                    catch (Exception ex) { rollbackErrors.Add(ex); }
                }
                if (rollbackErrors.Count != 0)
                {
                    retainTransaction = true;
                    List<Exception> failures = new List<Exception>();
                    failures.Add(activationError);
                    failures.AddRange(rollbackErrors);
                    throw new IOException("Desktop payload installation failed and rollback needs inspection at " +
                        transaction + ".", new AggregateException(failures));
                }
                throw;
            }
            finally
            {
                if (!retainTransaction)
                {
                    try
                    {
                        FileAttributes transactionAttributes;
                        if (TryGetExistingPathAttributes(transaction, out transactionAttributes))
                        {
                            if ((transactionAttributes & FileAttributes.Directory) == 0)
                                throw new IOException("Release payload transaction is not a directory: " + transaction);
                            AssertNoReparseAncestry(transaction, layout.Updates);
                            IOUtil.DeleteDirectoryWithin(transaction, layout.Updates);
                        }
                    }
                    catch
                    {
                        // Cleanup is best effort.  Preserve the activation or
                        // rollback exception and leave the verified transaction
                        // for a later repair instead of reporting a false failure.
                        retainTransaction = true;
                    }
                }
            }
        }

        private static bool TryGetExistingPathAttributes(string path,
            out FileAttributes attributes)
        {
            uint raw = NativeMethods.GetFileAttributes(ToExtendedPath(path));
            if (raw != NativeMethods.InvalidFileAttributes)
            {
                attributes = (FileAttributes)raw;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Release payload paths cannot be reparse points: " + path);
                return true;
            }
            int error = Marshal.GetLastWin32Error();
            attributes = 0;
            if (error == 2 || error == 3) return false;
            throw new Win32Exception(error, "Release payload path could not be inspected: " + path);
        }

        private static void MoveReleasePayloadPath(string source, string destination,
            bool directory)
        {
            FileAttributes sourceAttributes;
            if (!TryGetExistingPathAttributes(source, out sourceAttributes))
                throw new IOException("Release payload move source is missing: " + source);
            if (((sourceAttributes & FileAttributes.Directory) != 0) != directory)
                throw new IOException("Release payload move source has the wrong path type: " + source);
            FileAttributes destinationAttributes;
            if (TryGetExistingPathAttributes(destination, out destinationAttributes))
                throw new IOException("Release payload move destination already exists: " + destination);
            string parent = Path.GetDirectoryName(destination);
            FileAttributes parentAttributes;
            if (string.IsNullOrEmpty(parent) ||
                !TryGetExistingPathAttributes(parent, out parentAttributes) ||
                (parentAttributes & FileAttributes.Directory) == 0)
                throw new DirectoryNotFoundException("Release payload move destination parent is missing: " +
                    destination);
            if (!NativeMethods.MoveFile(ToExtendedPath(source), ToExtendedPath(destination)))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Release payload move failed: " + source + " -> " + destination);
            // MoveFileW succeeds only after the same-volume rename has committed.
            // Do not add a second existence check here: a transient inspection
            // failure after a successful move must still be treated as moved so
            // the transaction can roll it back instead of orphaning the payload.
        }
    }
}

namespace CodexPortable
{
    internal sealed class KeySetupResult
    {
        internal string ApiKey;
        internal string BaseUrl;
        internal string Model;

        internal void Clear()
        {
            ApiKey = null;
            BaseUrl = null;
            Model = null;
        }
    }

    internal sealed class KeySetupDialog : Form
    {
        private readonly TextBox keyBox;
        private readonly TextBox baseUrlBox;
        private readonly ComboBox modelBox;
        private readonly Label modelStatusLabel;
        private readonly System.Windows.Forms.Timer prefetchTimer;
        private KeySetupResult result;
        private bool prefetchRunning;
        private bool prefetchAgain;
        private int prefetchVersion;
        private string discoveredApiBase;
        private bool updatingBaseProgrammatically;
        private const int ModelPrefetchDelayMilliseconds = 800;

        private KeySetupDialog(string currentBaseUrl, string currentModel, string currentApiKey,
            IList<string> catalogModelIds)
        {
            Text = LauncherLocale.T("设置 API", "Configure API");
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 410);
            BackColor = Color.FromArgb(244, 246, 248);

            Panel header = new Panel();
            header.Location = new Point(0, 0);
            header.Size = new Size(ClientSize.Width, 70);
            header.BackColor = Color.FromArgb(15, 29, 48);
            Controls.Add(header);

            PictureBox brandMark = new PictureBox();
            brandMark.Location = new Point(24, 20);
            brandMark.Size = new Size(38, 38);
            brandMark.SizeMode = PictureBoxSizeMode.Zoom;
            using (Icon launcherIcon = PortableBranding.LoadLauncherIcon())
            {
                brandMark.Image = launcherIcon.ToBitmap();
            }
            header.Controls.Add(brandMark);

            Label headerTitle = new Label();
            headerTitle.Text = LauncherLocale.T("自定义 API", "Custom API");
            headerTitle.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
            headerTitle.ForeColor = Color.White;
            headerTitle.AutoSize = true;
            headerTitle.Location = new Point(76, 17);
            header.Controls.Add(headerTitle);

            AddLabel(LauncherLocale.T("Responses API 基础 URL", "Responses API Base URL"), 26, 84, 508);
            baseUrlBox = AddTextBox(26, 110, 508, true);
            baseUrlBox.MaxLength = 2048;
            baseUrlBox.Text = currentBaseUrl ?? "";

            AddLabel(LauncherLocale.T("API Key", "API key"), 26, 148, 508);
            keyBox = AddTextBox(26, 174, 508, true);
            keyBox.MaxLength = 1024;
            keyBox.UseSystemPasswordChar = true;
            keyBox.Text = currentApiKey ?? "";

            AddLabel(LauncherLocale.T("网关模型名 / 默认模型", "Gateway model / default model"), 26, 212, 508);
            modelBox = AddModelComboBox(26, 238, 508);
            modelBox.MaxLength = 512;
            if (catalogModelIds != null)
            {
                for (int i = 0; i < catalogModelIds.Count; i++)
                {
                    string catalogModel = catalogModelIds[i];
                    if (ProviderConfiguration.IsValidModel(catalogModel) &&
                        modelBox.Items.IndexOf(catalogModel) < 0)
                        modelBox.Items.Add(catalogModel);
                }
            }
            if (ProviderConfiguration.IsValidModel(currentModel) &&
                modelBox.Items.IndexOf(currentModel) < 0)
                modelBox.Items.Insert(0, currentModel);
            modelBox.Text = SelectDefaultModel(currentModel, modelBox.Items);

            modelStatusLabel = new Label();
            modelStatusLabel.Location = new Point(26, 282);
            modelStatusLabel.Size = new Size(508, 32);
            modelStatusLabel.Font = new Font(Font.FontFamily, 8F);
            modelStatusLabel.ForeColor = Color.FromArgb(100, 116, 139);
            modelStatusLabel.Text = LauncherLocale.T(
                "输入 API URL 与 Key 后会自动从网关读取模型列表，在框中选择默认模型；网关不可达时可手动输入。",
                "The model list is loaded from the gateway once you enter the API URL and key; choose the default model from the box, or type one when the gateway is unreachable.");
            Controls.Add(modelStatusLabel);

            prefetchTimer = new System.Windows.Forms.Timer();
            prefetchTimer.Interval = ModelPrefetchDelayMilliseconds;
            prefetchTimer.Tick += delegate
            {
                prefetchTimer.Stop();
                StartModelPrefetch();
            };
            baseUrlBox.TextChanged += delegate
            {
                if (!updatingBaseProgrammatically) ScheduleModelPrefetch();
            };
            keyBox.TextChanged += delegate { ScheduleModelPrefetch(); };
            ScheduleModelPrefetch();

            Button save = new Button();
            save.Text = LauncherLocale.T("保存", "Save");
            save.Location = new Point(344, 356);
            save.Size = new Size(90, 34);
            save.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            save.BackColor = Color.FromArgb(16, 163, 127);
            save.ForeColor = Color.White;
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderSize = 0;
            save.Cursor = Cursors.Hand;
            save.Click += SaveClicked;
            Controls.Add(save);

            Button cancel = new Button();
            cancel.Text = LauncherLocale.T("取消", "Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(444, 356);
            cancel.Size = new Size(90, 34);
            cancel.ForeColor = Color.FromArgb(30, 41, 59);
            cancel.BackColor = Color.White;
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            cancel.FlatAppearance.BorderSize = 1;
            cancel.Cursor = Cursors.Hand;
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = save;
        }

        private void AddLabel(string text, int x, int y, int width)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(width, 22);
            l.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(71, 85, 105);
            l.AutoEllipsis = true;
            Controls.Add(l);
        }

        private TextBox AddTextBox(int x, int y, int width, bool singleLine)
        {
            TextBox box = new TextBox();
            box.Location = new Point(x, y);
            box.Size = new Size(width, 27);
            box.Multiline = !singleLine;
            box.Font = new Font(Font.FontFamily, 9.5F);
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Color.White;
            box.ForeColor = Color.FromArgb(15, 23, 42);
            Controls.Add(box);
            return box;
        }

        private ComboBox AddModelComboBox(int x, int y, int width)
        {
            ComboBox box = new ComboBox();
            box.Location = new Point(x, y);
            box.Size = new Size(width, 27);
            box.DropDownStyle = ComboBoxStyle.DropDown;
            box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            box.AutoCompleteSource = AutoCompleteSource.ListItems;
            box.Font = new Font(Font.FontFamily, 9.5F);
            box.BackColor = Color.White;
            box.ForeColor = Color.FromArgb(15, 23, 42);
            Controls.Add(box);
            return box;
        }

        private static string SelectDefaultModel(string currentModel,
            ComboBox.ObjectCollection items)
        {
            if (ProviderConfiguration.IsValidModel(currentModel)) return currentModel;
            string fallback = ProviderConfiguration.DefaultModel;
            for (int i = 0; i < items.Count; i++)
            {
                string candidate = items[i] as string;
                if (string.Equals(candidate, fallback, StringComparison.Ordinal))
                    return fallback;
            }
            if (items.Count > 0) return items[0] as string;
            return fallback;
        }

        private void ScheduleModelPrefetch()
        {
            if (prefetchTimer == null) return;
            if (prefetchRunning)
            {
                prefetchAgain = true;
                return;
            }
            prefetchTimer.Stop();
            prefetchTimer.Start();
        }

        private void StartModelPrefetch()
        {
            if (prefetchRunning) return;
            string baseUrl;
            string key = keyBox.Text.Trim();
            if (!ProviderConfiguration.TryNormalizeBaseUrl(baseUrlBox.Text, out baseUrl) ||
                !ProviderConfiguration.IsValidApiKey(key)) return;
            prefetchRunning = true;
            int version = ++prefetchVersion;
            SetModelStatus(LauncherLocale.T("正在从网关读取模型列表…", "Loading model list from the gateway…"),
                Color.FromArgb(100, 116, 139));
            Task.Run(delegate
            {
                List<string> modelIds = null;
                string errorText = null;
                try
                {
                    string working;
                    if (ProviderConfiguration.TryDiscoverApiBase(baseUrl, key,
                            out working, out modelIds))
                        discoveredApiBase = working;
                }
                catch (Exception ex)
                {
                    errorText = ex.Message;
                }
                BeginInvoke((Action)delegate
                {
                    if (prefetchVersion != version) return;
                    prefetchRunning = false;
                    if (modelIds == null)
                    {
                        SetModelStatus(LauncherLocale.T("无法从网关读取模型列表（网关不可达？可手动输入模型名）。",
                            "Could not load the model list from the gateway (unreachable? you can type a model name)."),
                            Color.FromArgb(217, 119, 6));
                    }
                    else if (modelIds.Count == 0)
                    {
                        SetModelStatus(LauncherLocale.T("网关返回的模型列表为空。", "The gateway returned an empty model list."),
                            Color.FromArgb(190, 18, 60));
                    }
                    else
                    {
                        ApplyGatewayModelList(modelIds);
                        if (discoveredApiBase != null &&
                            !string.Equals(discoveredApiBase, baseUrl,
                                StringComparison.Ordinal))
                        {
                            updatingBaseProgrammatically = true;
                            try { baseUrlBox.Text = discoveredApiBase; }
                            finally { updatingBaseProgrammatically = false; }
                        }
                        SetModelStatus(LauncherLocale.T("已从网关读取 " + modelIds.Count.ToString() + " 个模型，请在框中选择默认模型。",
                            "Loaded " + modelIds.Count.ToString() + " models from the gateway; choose the default model."),
                            Color.FromArgb(5, 150, 105));
                    }
                    if (prefetchAgain)
                    {
                        prefetchAgain = false;
                        ScheduleModelPrefetch();
                    }
                });
            });
        }

        private void ApplyGatewayModelList(List<string> modelIds)
        {
            string previousText = modelBox.Text.Trim();
            modelBox.Items.Clear();
            for (int i = 0; i < modelIds.Count; i++)
            {
                if (ProviderConfiguration.IsValidModel(modelIds[i]) &&
                    modelBox.Items.IndexOf(modelIds[i]) < 0)
                    modelBox.Items.Add(modelIds[i]);
            }
            string selected = null;
            if (ProviderConfiguration.IsValidModel(previousText) &&
                modelBox.Items.IndexOf(previousText) >= 0)
                selected = previousText;
            if (selected == null &&
                modelBox.Items.IndexOf(ProviderConfiguration.DefaultModel) >= 0)
                selected = ProviderConfiguration.DefaultModel;
            if (selected == null && modelBox.Items.Count > 0)
                selected = modelBox.Items[0] as string;
            modelBox.Text = ProviderConfiguration.IsValidModel(selected) ? selected : previousText;
        }

        private void SetModelStatus(string text, Color color)
        {
            if (modelStatusLabel == null) return;
            modelStatusLabel.Text = text;
            modelStatusLabel.ForeColor = color;
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            string key = keyBox.Text.Trim();
            if (!ProviderConfiguration.IsValidApiKey(key))
            {
                MessageBox.Show(LauncherLocale.T("API Key 必须是 1–1024 个不含空格或换行的字符。", "API key must contain 1–1024 characters without spaces or line breaks."), LauncherLocale.T("设置自定义 API", "Custom API"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                keyBox.Focus();
                return;
            }
            string baseUrl;
            string rawBaseUrl = discoveredApiBase ?? baseUrlBox.Text;
            if (!ProviderConfiguration.TryNormalizeBaseUrl(rawBaseUrl, out baseUrl))
            {
                MessageBox.Show(LauncherLocale.T("Base URL 必须是绝对 HTTPS 地址；仅 localhost/127.0.0.1/::1 可使用 HTTP，且不能含账号、查询参数或片段。", "Base URL must be an absolute HTTPS address; HTTP is allowed only for localhost/127.0.0.1/::1, without credentials, query or fragment."), LauncherLocale.T("设置自定义 API", "Custom API"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                baseUrlBox.Focus();
                return;
            }
            string model = modelBox.Text.Trim();
            if (!ProviderConfiguration.IsValidModel(model))
            {
                MessageBox.Show(LauncherLocale.T("模型名必须是 1–512 个不含空格或换行的字符。", "Model name must contain 1–512 characters without spaces or line breaks."), LauncherLocale.T("设置自定义 API", "Custom API"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                modelBox.Focus();
                return;
            }
            result = new KeySetupResult();
            result.ApiKey = key;
            result.BaseUrl = baseUrl;
            result.Model = model;
            DialogResult = DialogResult.OK;
            Close();
        }

        internal static KeySetupResult Ask(IWin32Window owner, string currentBaseUrl, string currentModel,
            string currentApiKey, IList<string> catalogModelIds)
        {
            using (KeySetupDialog dialog = new KeySetupDialog(currentBaseUrl, currentModel,
                currentApiKey, catalogModelIds))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.result : null;
            }
        }
    }

    internal static class CryptoUtil
    {
        internal static void Zero(byte[] bytes)
        {
            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
        }
    }
}
