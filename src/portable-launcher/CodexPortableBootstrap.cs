// Codex Portable architecture bootstrapper.
// Build target: Windows x86, .NET Framework 4.8, C# 5 compatible.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("LF Portable")]
[assembly: AssemblyDescription("Architecture selector for LF Portable")]
[assembly: AssemblyCompany("LF")]
[assembly: AssemblyProduct("LF Portable")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyVersion("1.4.24.29")]
[assembly: AssemblyFileVersion("1.4.24.29")]
[assembly: ComVisible(false)]

namespace CodexPortableBootstrap
{
    internal static class BootLog
    {
        internal static void Write(string portableRoot, string message)
        {
            try
            {
                if (string.IsNullOrEmpty(portableRoot)) return;
                string logs = Path.Combine(portableRoot, "CodexData", "logs");
                Directory.CreateDirectory(logs);
                string file = Path.Combine(logs, "bootstrap-" +
                    DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                File.AppendAllText(file, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    " [" + message + "]" + Environment.NewLine);
            }
            catch { }
        }
    }

    internal static class Program
    {
        private const ushort ImageFileMachineI386 = 0x014c;
        private const ushort ImageFileMachineArm = 0x01c4;
        private const ushort ImageFileMachineAmd64 = 0x8664;
        private const ushort ImageFileMachineArm64 = 0xAA64;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--portable-root", StringComparison.OrdinalIgnoreCase) ||
                        args[i].StartsWith("--portable-root=", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("--portable-root is reserved for the Codex Portable bootstrapper.",
                            "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 41;
                    }
                    if (string.Equals(args[i], "--portable-root-token", StringComparison.OrdinalIgnoreCase) ||
                        args[i].StartsWith("--portable-root-token=", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("--portable-root-token is reserved for the LF Portable bootstrapper.",
                            "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 41;
                    }
                    if (string.Equals(args[i], "--bootstrapper-pid", StringComparison.OrdinalIgnoreCase) ||
                        args[i].StartsWith("--bootstrapper-pid=", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("--bootstrapper-pid is reserved for the LF Portable bootstrapper.",
                            "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 41;
                    }
                    if (string.Equals(args[i], "--bootstrapper-path", StringComparison.OrdinalIgnoreCase) ||
                        args[i].StartsWith("--bootstrapper-path=", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("--bootstrapper-path is reserved for the LF Portable bootstrapper.",
                            "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 41;
                    }
                }

                string root = Path.GetFullPath(Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location));
                string rootToken = PortableRootIdentity.GetExecutionRootToken(root);
                string architecture = DetectNativeArchitecture();
                BootLog.Write(root, "bootstrap start root=" + root + " arch=" + architecture);
                EnsureEmbeddedPayload(root, rootToken, architecture);
                BootLog.Write(root, "payload ensured, launching architecture launcher");
                List<string> childArguments = new List<string>();
                childArguments.Add("--portable-root");
                childArguments.Add(root);
                childArguments.Add("--portable-root-token");
                childArguments.Add(rootToken);
                childArguments.Add("--bootstrapper-pid");
                childArguments.Add(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                childArguments.Add("--bootstrapper-path");
                childArguments.Add(Assembly.GetExecutingAssembly().Location);
                // A formally assembled single-file release is the user-facing
                // entry point.  Carry an internal handoff hint so the
                // architecture launcher starts the desktop automatically after
                // its preparation checks instead of waiting for a second click.
                childArguments.Add("--auto-start");
                for (int i = 0; i < args.Length; i++) childArguments.Add(args[i]);

                string launchArguments = JoinArguments(childArguments);
                string variantDirectory = Path.Combine(root, "CodexData", "tools", "launchers");
                string primary = Path.Combine(variantDirectory, "CodexPortable." + architecture + ".exe");
                string fallback = Path.Combine(variantDirectory, "CodexPortable.x86.exe");
                string[] candidates = string.Equals(architecture, "x86", StringComparison.Ordinal) ?
                    new string[] { primary } : new string[] { primary, fallback };
                Exception lastStartError = null;
                for (int i = 0; i < candidates.Length; i++)
                {
                    string launcher = candidates[i];
                    if (!File.Exists(launcher)) continue;
                    try
                    {
                        ProcessStartInfo info = new ProcessStartInfo();
                        info.FileName = launcher;
                        info.Arguments = launchArguments;
                        info.WorkingDirectory = root;
                        info.UseShellExecute = false;
                        info.CreateNoWindow = true;
                        using (Process child = Process.Start(info))
                        {
                            if (child == null)
                                throw new InvalidOperationException("Unable to create launcher process.");
                            child.WaitForExit();
                            return child.ExitCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastStartError = ex;
                    }
                }

                MessageBox.Show("Codex Portable cannot start its " + architecture +
                    " launcher component or the x86 compatibility fallback. Rebuild or repair the portable program files." +
                    (lastStartError == null ? "" : "\r\n\r\n" + lastStartError.GetType().Name + ": " +
                        lastStartError.Message),
                    "Codex Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 40;
            }
            catch (Exception ex)
            {
                try
                {
                    string exe = Assembly.GetExecutingAssembly().Location;
                    BootLog.Write(Path.GetDirectoryName(exe), "bootstrap fatal " +
                        ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace);
                }
                catch { }
                MessageBox.Show("Codex Portable architecture bootstrap failed.\r\n\r\n" +
                    ex.GetType().Name + ": " + ex.Message, "Codex Portable",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 42;
            }
        }

        private static void EnsureEmbeddedPayload(string root, string rootToken,
            string architecture)
        {
            bool created;
            using (Mutex mutex = new Mutex(false,
                "Global\\LFPortable-Payload-" + rootToken, out created))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new IOException("Portable program preparation could not be serialized.");
                    EmbeddedPayload.EnsureReleaseInputs(root, rootToken, architecture);
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        private static string DetectNativeArchitecture()
        {
            try
            {
                ushort processMachine;
                ushort nativeMachine;
                if (IsWow64Process2(GetCurrentProcess(), out processMachine, out nativeMachine))
                {
                    ushort machine = nativeMachine == 0 ? processMachine : nativeMachine;
                    string result = NameForMachine(machine);
                    if (result != null) return result;
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            catch (BadImageFormatException) { }

            SYSTEM_INFO info;
            GetNativeSystemInfo(out info);
            switch (info.wProcessorArchitecture)
            {
                case 0: return "x86";
                case 5: return "arm";
                case 9: return "x64";
                case 12: return "arm64";
                default: return Environment.Is64BitOperatingSystem ? "x64" : "x86";
            }
        }

        private static string NameForMachine(ushort machine)
        {
            if (machine == ImageFileMachineI386) return "x86";
            if (machine == ImageFileMachineAmd64) return "x64";
            if (machine == ImageFileMachineArm) return "arm";
            if (machine == ImageFileMachineArm64) return "arm64";
            return null;
        }

        private static string JoinArguments(List<string> arguments)
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i != 0) result.Append(' ');
                result.Append(QuoteArgument(arguments[i]));
            }
            return result.ToString();
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return argument;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_INFO
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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("kernel32.dll")]
        private static extern void GetNativeSystemInfo(out SYSTEM_INFO systemInfo);
    }

    internal static class EmbeddedPayload
    {
        private const string DataPrefix = "CodexData/";
        private const long FreeSpaceReserve = 256L * 1024L * 1024L;
        private const int CopyBufferSize = 1024 * 1024;
        private const uint InvalidFileAttributes = 0xFFFFFFFFU;
        private const uint ProcessQueryLimitedInformation = 0x1000U;
        private const uint JobObjectQuery = 0x0004U;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        private sealed class PayloadEntry
        {
            internal ZipArchiveEntry ArchiveEntry;
            internal string RelativePath;
            internal string TargetPath;
        }

        internal static void EnsureReleaseInputs(string portableRoot, string rootToken,
            string architecture)
        {
            if (!string.Equals(architecture, "x86", StringComparison.Ordinal) &&
                !string.Equals(architecture, "x64", StringComparison.Ordinal) &&
                !string.Equals(architecture, "arm", StringComparison.Ordinal) &&
                !string.Equals(architecture, "arm64", StringComparison.Ordinal))
                throw new PlatformNotSupportedException(
                    "This Windows architecture is not supported by LF Portable.");
            string executable = Assembly.GetExecutingAssembly().Location;
            string dataRoot = Path.Combine(portableRoot, "CodexData");
            bool dataExists = Directory.Exists(dataRoot);
            FileStream source = null;
            Stream payload = null;
            ZipArchive archive = null;
            try
            {
                AssertPortableRootAncestry(portableRoot);
                if (dataExists) AssertExistingDirectoryIsSafe(dataRoot);
                else if (PathExists(dataRoot))
                    throw new IOException("A file blocks the CodexData directory.");
                CleanupStaleExtractionDirectories(portableRoot);
                source = new FileStream(executable, FileMode.Open, FileAccess.Read,
                    FileShare.Read, CopyBufferSize, FileOptions.RandomAccess);
                if (!TryOpenPayloadStream(source, out payload))
                {
                    throw new InvalidDataException(
                        "This Codex Portable executable has no embedded program payload.");
                }
                try
                {
                    archive = new ZipArchive(payload, ZipArchiveMode.Read, true);
                }
                catch (InvalidDataException)
                {
                    throw new InvalidDataException(
                        "This Codex Portable executable has no readable embedded program payload.");
                }

                string payloadArchitecture = DiscoverPayloadArchitecture(archive);
                // x64 and ARM64 releases must not cross-install.  Keep the
                // x86/ARM32 path available so its x86 diagnostic launcher can
                // explain that no official 32-bit desktop package is shipped.
                if ((string.Equals(architecture, "x64", StringComparison.Ordinal) ||
                    string.Equals(architecture, "arm64", StringComparison.Ordinal)) &&
                    !string.Equals(architecture, payloadArchitecture,
                        StringComparison.Ordinal))
                    throw new PlatformNotSupportedException(
                        "This executable contains the " + payloadArchitecture +
                        " Codex Desktop package, but Windows is " + architecture + ".");
                string launcherArchitecture =
                    string.Equals(architecture, "x64", StringComparison.Ordinal) ||
                    string.Equals(architecture, "arm64", StringComparison.Ordinal) ?
                    architecture : "x86";
                List<PayloadEntry> releaseEntries = ReadPayloadEntries(archive,
                    portableRoot, payloadArchitecture);
                Version embeddedVersion = ReadFileVersion(executable);
                bool replaceExisting = dataExists && (!ExistingReleaseVersionMatches(
                    portableRoot, launcherArchitecture, payloadArchitecture,
                    embeddedVersion) || ReleaseInputsDiffer(releaseEntries));
                List<PayloadEntry> planned = BuildExtractionPlan(releaseEntries,
                    portableRoot, replaceExisting, launcherArchitecture);
                if (planned.Count == 0) return;

                long totalBytes = 0;
                for (int i = 0; i < planned.Count; i++)
                    totalBytes = checked(totalBytes + planned[i].ArchiveEntry.Length);
                EnsureFreeSpace(portableRoot, totalBytes);
                BootLog.Write(portableRoot, "plan built replace=" + replaceExisting +
                    " entries=" + planned.Count.ToString(CultureInfo.InvariantCulture) +
                    " bytes=" + totalBytes.ToString(CultureInfo.InvariantCulture));
                ExtractReleaseInputs(portableRoot, dataRoot, dataExists, rootToken,
                    replaceExisting, planned, totalBytes);
                BootLog.Write(portableRoot, "ExtractReleaseInputs returned");
            }
            finally
            {
                if (archive != null) archive.Dispose();
                if (payload != null) payload.Dispose();
                if (source != null) source.Dispose();
            }
        }

        private static bool TryOpenPayloadStream(FileStream source, out Stream payload)
        {
            payload = null;
            const int endRecordSize = 22;
            const int maximumCommentSize = 65535;
            if (source.Length < endRecordSize) return false;
            int tailSize = (int)Math.Min(source.Length,
                endRecordSize + maximumCommentSize);
            byte[] tail = new byte[tailSize];
            source.Position = source.Length - tailSize;
            int received = 0;
            while (received < tail.Length)
            {
                int read = source.Read(tail, received, tail.Length - received);
                if (read == 0) return false;
                received += read;
            }

            for (int offset = tail.Length - endRecordSize; offset >= 0; offset--)
            {
                if (ReadUInt32(tail, offset) != 0x06054B50U) continue;
                int commentLength = ReadUInt16(tail, offset + 20);
                if (offset + endRecordSize + commentLength != tail.Length) continue;
                if (ReadUInt16(tail, offset + 4) != 0 ||
                    ReadUInt16(tail, offset + 6) != 0)
                    throw new InvalidDataException(
                        "Multi-volume embedded payloads are not supported.");
                uint centralSize = ReadUInt32(tail, offset + 12);
                uint centralOffset = ReadUInt32(tail, offset + 16);
                if (centralSize == UInt32.MaxValue || centralOffset == UInt32.MaxValue)
                    throw new InvalidDataException(
                        "ZIP64 embedded payloads are not supported by this bootstrapper.");
                long absoluteEndRecord = source.Length - tail.Length + offset;
                long archiveStart = absoluteEndRecord - centralSize - centralOffset;
                if (archiveStart < 0 || archiveStart >= source.Length) continue;
                payload = new ReadOnlySegmentStream(source, archiveStart,
                    source.Length - archiveStart);
                return true;
            }
            return false;
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | buffer[offset + 1] << 8);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | buffer[offset + 1] << 8 |
                buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
        }

        private static string DiscoverPayloadArchitecture(ZipArchive archive)
        {
            string discovered = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relative = NormalizeArchivePath(entry.FullName);
                if (IsDirectory(entry)) continue;
                string architecture = null;
                if (string.Equals(relative,
                    "CodexData/packages/LFPortable-x64.msix",
                    StringComparison.OrdinalIgnoreCase)) architecture = "x64";
                else if (string.Equals(relative,
                    "CodexData/packages/LFPortable-arm64.msix",
                    StringComparison.OrdinalIgnoreCase)) architecture = "arm64";
                if (architecture == null) continue;
                if (discovered != null)
                    throw new InvalidDataException(
                        "The embedded payload contains more than one desktop architecture.");
                discovered = architecture;
            }
            if (discovered == null)
                throw new InvalidDataException(
                    "The embedded payload has no supported desktop package.");
            return discovered;
        }

        private static List<PayloadEntry> ReadPayloadEntries(ZipArchive archive,
            string portableRoot, string architecture)
        {
            string[] requiredFiles = new string[] {
                "CodexData/README.txt",
                "CodexData/THIRD_PARTY.txt",
                "CodexData/tools/launchers/CodexPortable.x86.exe",
                "CodexData/tools/launchers/CodexPortable.x64.exe",
                "CodexData/tools/launchers/CodexPortable.arm64.exe",
                "CodexData/packages/LFPortable-common.zip",
                "CodexData/packages/LFPortable-" + architecture + ".msix"
            };
            HashSet<string> missingRequired = new HashSet<string>(requiredFiles,
                StringComparer.OrdinalIgnoreCase);
            List<PayloadEntry> result = new List<PayloadEntry>();
            HashSet<string> archivePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relative = NormalizeArchivePath(entry.FullName);
                if (relative.Length == 0) continue;
                bool directory = IsDirectory(entry);
                AssertArchiveEntryType(entry, directory);
                if (!IsAllowedReleasePath(relative, directory, architecture))
                    throw new InvalidDataException(
                        "The embedded payload contains an unexpected path: " + relative);
                if (!archivePaths.Add(relative))
                    throw new InvalidDataException(
                        "The embedded payload contains a duplicate path: " + relative);
                if (directory) continue;
                if (!missingRequired.Remove(relative))
                    throw new InvalidDataException(
                        "The embedded payload contains an unexpected release file: " +
                        relative);
                result.Add(new PayloadEntry {
                    ArchiveEntry = entry,
                    RelativePath = relative,
                    TargetPath = ResolveTargetPath(portableRoot, relative)
                });
            }
            if (missingRequired.Count != 0)
                throw new InvalidDataException(
                    "The embedded payload is missing a required portable release input.");
            return result;
        }

        private static List<PayloadEntry> BuildExtractionPlan(
            List<PayloadEntry> releaseEntries, string portableRoot,
            bool replaceExisting, string architecture)
        {
            List<PayloadEntry> planned = new List<PayloadEntry>();
            for (int i = 0; i < releaseEntries.Count; i++)
            {
                PayloadEntry item = releaseEntries[i];
                if (PathExists(item.TargetPath))
                {
                    AssertSafeExistingAncestry(portableRoot,
                        Path.GetDirectoryName(item.TargetPath));
                    AssertExistingPathIsRegularFile(item.TargetPath);
                    // The launchers are small and carry the upgrade logic. A
                    // same-version rebuild can keep their length unchanged, so
                    // compare launcher bytes instead of trusting version or
                    // length. Identical launchers need no staging or rewrite;
                    // changed launchers are still refreshed on every upgrade.
                    if (!replaceExisting && IsLauncherReleaseInput(item.RelativePath))
                    {
                        if (IsByteIdentical(item)) continue;
                    }
                    else if (!replaceExisting &&
                        new FileInfo(item.TargetPath).Length == item.ArchiveEntry.Length)
                        continue;
                }
                planned.Add(item);
            }
            string marker = "CodexData/tools/launchers/CodexPortable." +
                architecture + ".exe";
            planned.Sort(delegate(PayloadEntry left, PayloadEntry right)
            {
                bool leftMarker = string.Equals(left.RelativePath, marker,
                    StringComparison.OrdinalIgnoreCase);
                bool rightMarker = string.Equals(right.RelativePath, marker,
                    StringComparison.OrdinalIgnoreCase);
                if (leftMarker != rightMarker) return leftMarker ? 1 : -1;
                return string.CompareOrdinal(left.RelativePath, right.RelativePath);
            });
            return planned;
        }

        private static bool IsByteIdentical(PayloadEntry item)
        {
            if (item == null || !PathExists(item.TargetPath)) return false;
            try
            {
                FileInfo existing = new FileInfo(item.TargetPath);
                if (existing.Length != item.ArchiveEntry.Length) return false;
                const int bufferSize = 64 * 1024;
                byte[] expected = new byte[bufferSize];
                byte[] actual = new byte[bufferSize];
                try
                {
                    using (Stream source = item.ArchiveEntry.Open())
                    using (FileStream target = new FileStream(item.TargetPath, FileMode.Open,
                        FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan))
                    {
                        while (true)
                        {
                            int expectedRead = ReadChunk(source, expected);
                            int actualRead = ReadChunk(target, actual);
                            if (expectedRead != actualRead) return false;
                            if (expectedRead == 0) return true;
                            for (int i = 0; i < expectedRead; i++)
                                if (expected[i] != actual[i]) return false;
                        }
                    }
                }
                finally
                {
                    Array.Clear(expected, 0, expected.Length);
                    Array.Clear(actual, 0, actual.Length);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        private static int ReadChunk(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        // Launcher-only releases carry an identical payload (same MSIX/common
        // package bytes).  Persisting each payload entry's size under a base
        // identity lets later runs skip re-copying megabytes of unchanged data
        // on slow USB media.  PayloadIdentity MUST be bumped together with the
        // release base (see build/release-base-*): a real base change without
        // a new identity would otherwise keep stale same-length packages.
        private const string PayloadFingerprintFileName = "payload-fingerprints.txt";
        private const string PayloadIdentity = "codex-26.901.5003.0";
        private const long PayloadFingerprintSkipMinimumBytes = 16L * 1024L * 1024L;

        private static string PayloadFingerprintPath(string dataRoot)
        {
            return Path.Combine(dataRoot, PayloadFingerprintFileName);
        }

        private static void SavePayloadFingerprints(string dataRoot,
            List<PayloadEntry> entries)
        {
            try
            {
                if (dataRoot == null || entries == null || entries.Count == 0) return;
                string path = PayloadFingerprintPath(dataRoot);
                string temporary = path + ".tmp";
                using (StreamWriter writer = new StreamWriter(temporary, false,
                    new UTF8Encoding(false)))
                {
                    writer.Write("#id ");
                    writer.Write(PayloadIdentity);
                    writer.WriteLine();
                    for (int i = 0; i < entries.Count; i++)
                    {
                        ZipArchiveEntry entry = entries[i].ArchiveEntry;
                        writer.Write(entries[i].RelativePath);
                        writer.Write('\t');
                        writer.Write(entry.Length.ToString(
                            CultureInfo.InvariantCulture));
                        writer.WriteLine();
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch
            {
                // A missing fingerprint only costs one full re-copy later.
            }
        }

        private static void LoadPayloadFingerprints(string dataRoot,
            List<PayloadEntry> entries, Dictionary<string, bool> skip)
        {
            if (dataRoot == null || entries == null || skip == null) return;
            string path = PayloadFingerprintPath(dataRoot);
            if (!File.Exists(path)) return;
            Dictionary<string, long> known = new Dictionary<string, long>(
                StringComparer.Ordinal);
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0) return;
                if (!lines[0].StartsWith("#id ", StringComparison.Ordinal) ||
                    !string.Equals(lines[0].Substring(4).Trim(), PayloadIdentity,
                        StringComparison.Ordinal))
                    return;
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int first = line.IndexOf('\t');
                    if (first <= 0) continue;
                    long markerLength;
                    if (!long.TryParse(line.Substring(first + 1), NumberStyles.None,
                            CultureInfo.InvariantCulture, out markerLength))
                        continue;
                    known[line.Substring(0, first)] = markerLength;
                }
            }
            catch { return; }
            for (int i = 0; i < entries.Count; i++)
            {
                string relativePath = entries[i].RelativePath;
                long markerLength;
                if (!known.TryGetValue(relativePath, out markerLength)) continue;
                ZipArchiveEntry archiveEntry = entries[i].ArchiveEntry;
                if (archiveEntry == null ||
                    archiveEntry.Length < PayloadFingerprintSkipMinimumBytes ||
                    archiveEntry.Length != markerLength) continue;
                string target = TargetForEntry(dataRoot, relativePath);
                try
                {
                    if (File.Exists(target) &&
                        new FileInfo(target).Length == markerLength)
                        skip[relativePath] = true;
                }
                catch { }
            }
        }

        private static string TargetForEntry(string dataRoot, string relativePath)
        {
            return Path.Combine(dataRoot,
                relativePath.Substring(DataPrefix.Length)
                    .Replace('/', Path.DirectorySeparatorChar));
        }

        private static Version ReadFileVersion(string path)
        {
            string text = FileVersionInfo.GetVersionInfo(path).FileVersion;
            Version version;
            if (string.IsNullOrEmpty(text) || !Version.TryParse(text, out version))
                throw new InvalidDataException(
                    "A portable launcher file has no usable release version: " + path);
            return version;
        }

        private static bool ExistingReleaseVersionMatches(string portableRoot,
            string launcherArchitecture, string payloadArchitecture,
            Version embeddedVersion)
        {
            string launcher = Path.Combine(portableRoot, "CodexData", "tools",
                "launchers", "CodexPortable." + launcherArchitecture + ".exe");
            if (!PathExists(launcher)) return false;
            AssertSafeExistingAncestry(portableRoot, Path.GetDirectoryName(launcher));
            AssertExistingPathIsRegularFile(launcher);
            string package = Path.Combine(portableRoot, "CodexData", "packages",
                "LFPortable-" + payloadArchitecture + ".msix");
            string obsoletePackage = Path.Combine(portableRoot, "CodexData", "packages",
                "LFPortable-" + (string.Equals(payloadArchitecture, "x64",
                    StringComparison.Ordinal) ? "arm64" : "x64") + ".msix");
            if (!PathExists(package) || PathExists(obsoletePackage)) return false;
            AssertSafeExistingAncestry(portableRoot, Path.GetDirectoryName(package));
            AssertExistingPathIsRegularFile(package);
            try { return ReadFileVersion(launcher).Equals(embeddedVersion); }
            catch (InvalidDataException) { return false; }
            catch (IOException) { return false; }
        }

        private static bool ReleaseInputsDiffer(List<PayloadEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                PayloadEntry item = entries[i];
                if (!PathExists(item.TargetPath)) return true;
                try
                {
                    AssertExistingPathIsRegularFile(item.TargetPath);
                    if (new FileInfo(item.TargetPath).Length != item.ArchiveEntry.Length)
                        return true;
                }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
            return false;
        }

        private sealed class DerivedStateEntry
        {
            internal string RelativePath;
            internal string TargetPath;
            internal string BackupPath;
            internal bool Directory;
        }

        private static readonly string[] FixedDerivedDirectories = new string[] {
            "CodexData/app",
            "CodexData/tools/desktop-payloads",
            "CodexData/tools/dotnet",
            "CodexData/tools/gh",
            "CodexData/data/profile/.cache/codex-runtimes/codex-primary-runtime",
            "CodexData/data/profile/.codex/offline-marketplaces/openai-primary-runtime",
            "CodexData/data/profile/.codex/plugins/cache/openai-primary-runtime",
            "CodexData/data/profile/.codex/plugins/repair-backups",
            "CodexData/updates"
        };

        private static readonly string[] FixedDerivedFiles = new string[] {
            "CodexData/portable-release.json",
            "CodexData/portable-package-manifest.json",
            "portable-package-manifest.json"
        };

        // The whole release extraction runs on a background thread while the
        // progress window keeps pumping on the UI thread.  A slow USB volume
        // can stall an individual file-system call for many seconds (large
        // renames, antivirus scanning); doing that on the UI thread is what
        // turned the window into an unresponsive ghost the moment it was
        // clicked.  All progress calls from the worker are marshaled back.
        private static void ExtractReleaseInputs(string portableRoot, string dataRoot,
            bool dataExists, string rootToken, bool replaceExisting,
            List<PayloadEntry> entries, long totalBytes)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (PayloadProgressForm progress = new PayloadProgressForm())
            {
                progress.Show();
                Exception failure = null;
                bool done = false;
                Thread worker = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        ExtractReleaseInputsCore(portableRoot, dataRoot, dataExists,
                            rootToken, replaceExisting, entries, totalBytes, progress);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { done = true; }
                }));
                worker.IsBackground = true;
                worker.Name = "LF Portable extract worker";
                worker.Start();
                while (!done)
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                }
                Application.DoEvents();
                if (failure != null)
                {
                    progress.Hide();
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                        failure).Throw();
                }
            }
        }

        private static void ExtractReleaseInputsCore(string portableRoot, string dataRoot,
            bool dataExists, string rootToken, bool replaceExisting,
            List<PayloadEntry> entries, long totalBytes, PayloadProgressForm progress)
        {
            string stagingName = ".CodexData.extracting-" +
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N");
            string stagingRoot = Path.Combine(portableRoot, stagingName);
            BootLog.Write(portableRoot, "extract begin dataExists=" + dataExists);
            Dictionary<string, bool> payloadSkip = new Dictionary<string, bool>(
                StringComparer.Ordinal);
            LoadPayloadFingerprints(dataRoot, entries, payloadSkip);
            List<DerivedStateEntry> movedDerived = null;
            bool retainStaging = false;
            Mutex mutation = null;
            bool mutationAcquired = false;
            try
            {
                EnsureSafeDirectory(portableRoot, stagingRoot);
                progress.UpdateProgress(0, totalBytes, 0, entries.Count);

                byte[] buffer = new byte[CopyBufferSize];
                long completedBytes = 0;
                try
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        PayloadEntry item = entries[i];
                        if (payloadSkip.ContainsKey(item.RelativePath))
                        {
                            completedBytes = checked(completedBytes +
                                item.ArchiveEntry.Length);
                            progress.UpdateProgress(completedBytes, totalBytes, i + 1,
                                entries.Count);
                            BootLog.Write(portableRoot,
                                "payload skip (identical) " + item.RelativePath);
                            continue;
                        }
                        string staged = Path.Combine(stagingRoot,
                            item.RelativePath.Substring(DataPrefix.Length)
                                .Replace('/', Path.DirectorySeparatorChar));
                        BootLog.Write(portableRoot, "payload entry " + item.RelativePath);
                        string stagedParent = Path.GetDirectoryName(staged);
                        EnsureSafeDirectory(stagingRoot, stagedParent);
                        long written = 0;
                        using (Stream input = item.ArchiveEntry.Open())
                        using (FileStream output = new FileStream(staged, FileMode.CreateNew,
                            FileAccess.Write, FileShare.None, CopyBufferSize,
                            FileOptions.SequentialScan))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                output.Write(buffer, 0, read);
                                written = checked(written + read);
                                completedBytes = checked(completedBytes + read);
                                progress.UpdateProgress(completedBytes, totalBytes, i,
                                    entries.Count);
                            }
                            // Never fsync the payload write: a slow or
                            // briefly unresponsive removable volume can stall
                            // indefinitely on FLUSH_CACHE.  The stream is
                            // verified by length and repaired by the next run.
                            output.Flush();
                        }
                        if (written != item.ArchiveEntry.Length)
                            throw new InvalidDataException(
                                "The embedded payload entry ended unexpectedly: " +
                                item.RelativePath);
                        progress.UpdateProgress(completedBytes, totalBytes, i + 1,
                            entries.Count);
                    }
                }
                finally { Array.Clear(buffer, 0, buffer.Length); }

                if (!dataExists)
                {
                    if (PathExists(dataRoot))
                        throw new IOException("CodexData appeared while it was being prepared.");
                    Directory.Move(stagingRoot, dataRoot);
                    SavePayloadFingerprints(dataRoot, entries);
                    return;
                }

                if (entries.Count != 0)
                {
                    if (progress != null)
                        progress.UpdateStatus("正在更新程序文件 / Updating program files");
                    BootLog.Write(portableRoot, "mutation acquire begin");
                    mutation = new Mutex(false,
                        "Global\\CodexPortable-Desktop-" + rootToken + "-mutation");
                    try { mutationAcquired = mutation.WaitOne(0, false); }
                    catch (AbandonedMutexException) { mutationAcquired = true; }
                    if (!mutationAcquired)
                        throw new IOException(
                            "Another portable installation or repair is in progress.");
                    // Never remove a package-owned tree while the matching portable
                    // desktop is alive. The job and executable-path checks cover
                    // both current and older launcher handoffs.
                    EnsurePortableDesktopStopped(portableRoot, rootToken);
                    if (progress != null) progress.Pump();
                }
                if (replaceExisting)
                {
                    BootLog.Write(portableRoot, "move-derived begin");
                    // Move package-owned derived state out of the way before any
                    // release input is replaced. User data, logs, keys, SQLite,
                    // and unknown entries below CodexData are never visited.
                    movedDerived = new List<DerivedStateEntry>();
                    MoveDerivedStateToBackup(portableRoot, stagingRoot,
                        movedDerived);
                    BootLog.Write(portableRoot, "move-derived done moved=" +
                        (movedDerived == null ? 0 : movedDerived.Count).ToString(
                            CultureInfo.InvariantCulture));
                    if (progress != null) progress.Pump();
                }

                // Each release-owned file is replaced with a same-volume rename.
                for (int i = 0; i < entries.Count; i++)
                {
                    PayloadEntry item = entries[i];
                    if (payloadSkip.ContainsKey(item.RelativePath))
                    {
                        BootLog.Write(portableRoot,
                            "committed (unchanged) " + item.RelativePath);
                        continue;
                    }
                    string staged = Path.Combine(stagingRoot,
                        item.RelativePath.Substring(DataPrefix.Length)
                            .Replace('/', Path.DirectorySeparatorChar));
                    string targetParent = Path.GetDirectoryName(item.TargetPath);
                    EnsureSafeDirectory(dataRoot, targetParent);
                    bool replaceItem = replaceExisting ||
                        IsLauncherReleaseInput(item.RelativePath);
                    if (PathExists(item.TargetPath))
                    {
                        AssertExistingPathIsRegularFile(item.TargetPath);
                        if (!replaceItem)
                        {
                            File.Delete(staged);
                            continue;
                        }
                    }
                    if (replaceItem && item.ArchiveEntry.Length > LargeCommitStreamThreshold &&
                        File.Exists(staged))
                    {
                        // Skip the streaming commit when the staged payload is
                        // already identical to what is installed (launcher-only
                        // bumps); otherwise stream the large input into place
                        // with visible progress.
                        if (PathExists(item.TargetPath) &&
                            StagedInputMatchesExisting(staged, item.TargetPath))
                        {
                            File.Delete(staged);
                        }
                        else
                        {
                            CommitStagedLargeFile(staged, item.TargetPath, progress);
                        }
                    }
                    else
                    {
                        MoveAtomically(staged, item.TargetPath, replaceItem);
                    }
                    if (progress != null) progress.Pump();
                    BootLog.Write(portableRoot, "committed " + item.RelativePath);
                }
                SavePayloadFingerprints(dataRoot, entries);
                if (progress != null)
                    progress.UpdateStatus("正在准备 Codex Portable / Preparing Codex Portable");
                BootLog.Write(portableRoot, "commit loop done");
            }
            catch (Exception upgradeError)
            {
                if (movedDerived != null && movedDerived.Count != 0)
                {
                    try { RestoreDerivedState(movedDerived, portableRoot); }
                    catch (Exception restoreError)
                    {
                        retainStaging = true;
                        throw new IOException(
                            "Portable upgrade failed and package-owned state could not be restored. " +
                            "It was preserved for the next repair attempt: " + stagingRoot,
                            new AggregateException(upgradeError, restoreError));
                    }
                }
                throw;
            }
            finally
            {
                BootLog.Write(portableRoot, "extract core finished");
                if (mutation != null)
                {
                    try { if (mutationAcquired) mutation.ReleaseMutex(); }
                    finally { mutation.Dispose(); }
                }
                if (!retainStaging && PathExists(stagingRoot))
                    ScheduleStagingCleanup(stagingRoot);
            }
        }

        private static void ScheduleStagingCleanup(string stagingRoot)
        {
            // Replacing a package-owned directory is a same-volume rename, but
            // deleting the old tree can involve hundreds of thousands of files
            // on a USB volume. Do not keep the extraction dialog's UI thread in
            // that slow path after the new release inputs are already active.
            try
            {
                Thread cleanup = new Thread(new ThreadStart(delegate
                {
                    try { DeleteDirectoryTree(stagingRoot); }
                    catch { }
                }));
                cleanup.Name = "LF Portable staging cleanup";
                cleanup.IsBackground = true;
                cleanup.Start();
            }
            catch
            {
                // A completed swap remains usable even if a cleanup thread
                // cannot be created. The next bootstrap run removes stale
                // extraction directories before inspecting the payload.
            }
        }

        private static bool IsLauncherReleaseInput(string relativePath)
        {
            return relativePath.StartsWith("CodexData/tools/launchers/",
                    StringComparison.OrdinalIgnoreCase) &&
                relativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                relativePath.IndexOf('/', "CodexData/tools/launchers/".Length) < 0;
        }

        private static void MoveDerivedStateToBackup(string portableRoot,
            string stagingRoot, List<DerivedStateEntry> moved)
        {
            List<string> relativePaths = new List<string>();
            relativePaths.AddRange(FixedDerivedDirectories);
            relativePaths.AddRange(FixedDerivedFiles);
            AddStagedMarketplacePaths(stagingRoot, relativePaths);
            AddExistingMarketplacePaths(portableRoot, relativePaths);
            string x64Package = "CodexData/packages/LFPortable-x64.msix";
            string arm64Package = "CodexData/packages/LFPortable-arm64.msix";
            string stagedX64 = Path.Combine(stagingRoot,
                x64Package.Substring(DataPrefix.Length)
                    .Replace('/', Path.DirectorySeparatorChar));
            relativePaths.Add(File.Exists(stagedX64) ? arm64Package : x64Package);

            string backupRoot = Path.Combine(stagingRoot, ".derived");
            EnsureSafeDirectory(stagingRoot, backupRoot);
            HashSet<string> expectedDirectories = new HashSet<string>(
                FixedDerivedDirectories, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < relativePaths.Count; i++)
            {
                string relative = relativePaths[i];
                string target = ResolveTargetPath(portableRoot, relative);
                if (!PathExists(target)) continue;
                AssertSafeExistingAncestry(portableRoot, Path.GetDirectoryName(target));
                bool expectedDirectory = expectedDirectories.Contains(relative) ||
                    relative.StartsWith("CodexData/data/profile/.codex/offline-marketplaces/",
                        StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("CodexData/data/profile/.codex/plugins/cache/",
                        StringComparison.OrdinalIgnoreCase);
                FileAttributes attributes = File.GetAttributes(target);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (directory != expectedDirectory)
                    throw new IOException("Portable upgrade found a package-owned path with the wrong type: " +
                        target);
                if (directory) AssertExistingDirectoryIsSafe(target);
                else AssertExistingPathIsRegularFile(target);

                string backupRelative = relative.StartsWith(DataPrefix,
                    StringComparison.OrdinalIgnoreCase) ?
                    relative.Substring(DataPrefix.Length) :
                    Path.Combine(".root", relative);
                string backup = Path.Combine(backupRoot,
                    backupRelative.Replace('/', Path.DirectorySeparatorChar));
                EnsureSafeDirectory(backupRoot, Path.GetDirectoryName(backup));
                if (directory) Directory.Move(target, backup);
                else File.Move(target, backup);
                moved.Add(new DerivedStateEntry {
                    RelativePath = relative,
                    TargetPath = target,
                    BackupPath = backup,
                    Directory = directory
                });
            }
        }

        private static void RestoreDerivedState(List<DerivedStateEntry> moved,
            string portableRoot)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                DerivedStateEntry item = moved[i];
                if (!PathExists(item.BackupPath)) continue;
                string parent = Path.GetDirectoryName(item.TargetPath);
                EnsureSafeDirectory(portableRoot, parent);
                if (PathExists(item.TargetPath))
                    throw new IOException("Portable upgrade restore target is occupied: " +
                        item.TargetPath);
                if (item.Directory) Directory.Move(item.BackupPath, item.TargetPath);
                else File.Move(item.BackupPath, item.TargetPath);
            }
        }

        private static void AddStagedMarketplacePaths(string stagingRoot,
            List<string> relativePaths)
        {
            string packages = Path.Combine(stagingRoot, "packages");
            // Fingerprint-skipped runs stage no package files at all; their
            // marketplace trees are already installed and must not be moved.
            if (!Directory.Exists(packages)) return;
            AddMarketplacePathsFromArchive(
                Path.Combine(packages, "LFPortable-common.zip"),
                "data/profile/.codex/offline-marketplaces/", relativePaths);
            string[] desktopPackages = Directory.GetFiles(packages,
                "LFPortable-*.msix", SearchOption.TopDirectoryOnly);
            if (desktopPackages.Length != 1)
                throw new InvalidDataException(
                    "The staged payload must contain one desktop package.");
            AddMarketplacePathsFromArchive(desktopPackages[0],
                "app/resources/plugins/", relativePaths);
        }

        private static void AddExistingMarketplacePaths(string portableRoot,
            List<string> relativePaths)
        {
            string packages = Path.Combine(portableRoot, "CodexData", "packages");
            AddExistingMarketplaceArchive(Path.Combine(packages,
                "LFPortable-common.zip"),
                "data/profile/.codex/offline-marketplaces/", relativePaths);
            AddExistingMarketplaceArchive(Path.Combine(packages,
                "LFPortable-x64.msix"), "app/resources/plugins/", relativePaths);
            AddExistingMarketplaceArchive(Path.Combine(packages,
                "LFPortable-arm64.msix"), "app/resources/plugins/", relativePaths);
        }

        private static void AddExistingMarketplaceArchive(string package,
            string prefix, List<string> relativePaths)
        {
            if (!PathExists(package)) return;
            AssertSafeExistingAncestry(Path.GetDirectoryName(
                Path.GetDirectoryName(package)), Path.GetDirectoryName(package));
            AssertExistingPathIsRegularFile(package);
            try { AddMarketplacePathsFromArchive(package, prefix, relativePaths); }
            catch (InvalidDataException) { }
        }

        private static void AddExistingMarketplaceRoot(string root,
            List<string> relativePaths)
        {
            if (!PathExists(root)) return;
            AssertExistingDirectoryIsSafe(root);
            string[] catalogs = Directory.GetDirectories(root, "*",
                SearchOption.TopDirectoryOnly);
            for (int i = 0; i < catalogs.Length; i++)
            {
                AssertExistingDirectoryIsSafe(catalogs[i]);
                string marker = Path.Combine(catalogs[i], ".agents", "plugins",
                    "marketplace.json");
                if (!PathExists(marker)) continue;
                AssertSafeExistingAncestry(root, Path.GetDirectoryName(marker));
                AssertExistingPathIsRegularFile(marker);
                AddMarketplaceDerivedPaths(Path.GetFileName(catalogs[i]), relativePaths);
            }
        }

        private static void AddMarketplacePathsFromArchive(string package,
            string prefix, List<string> relativePaths)
        {
            const string suffix = "/.agents/plugins/marketplace.json";
            // The package may have been fingerprint-skipped this run (identical
            // payload already installed), so the staged copy does not exist;
            // its marketplace paths are already in place then.
            if (!File.Exists(package)) return;
            using (FileStream stream = new FileStream(package, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = (entry.FullName ?? "").Replace('\\', '/');
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    string catalog = name.Substring(prefix.Length,
                        name.Length - prefix.Length - suffix.Length);
                    if (!IsSafeMarketplaceName(catalog))
                        throw new InvalidDataException(
                            "The embedded package contains an unsafe marketplace name.");
                    AddMarketplaceDerivedPaths(catalog, relativePaths);
                }
            }
        }

        private static void AddMarketplaceDerivedPaths(string catalog,
            List<string> relativePaths)
        {
            if (!IsSafeMarketplaceName(catalog))
                throw new InvalidDataException(
                    "A portable marketplace has an unsafe catalog name.");
            string[] catalogPaths = new string[] {
                "CodexData/data/profile/.codex/offline-marketplaces/" + catalog,
                "CodexData/data/profile/.codex/plugins/cache/" + catalog
            };
            for (int pathIndex = 0; pathIndex < catalogPaths.Length; pathIndex++)
            {
                string path = catalogPaths[pathIndex];
                bool present = false;
                for (int i = 0; i < relativePaths.Count; i++)
                    if (string.Equals(relativePaths[i], path,
                        StringComparison.OrdinalIgnoreCase)) { present = true; break; }
                if (!present) relativePaths.Add(path);
            }
        }

        private static bool IsSafeMarketplaceName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128 ||
                string.Equals(value, ".", StringComparison.Ordinal) ||
                string.Equals(value, "..", StringComparison.Ordinal) ||
                value.EndsWith(".", StringComparison.Ordinal) ||
                value.EndsWith(" ", StringComparison.Ordinal)) return false;
            string stem = value;
            int extension = stem.IndexOf('.');
            if (extension >= 0) stem = stem.Substring(0, extension);
            stem = stem.ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                stem == "CLOCK$" ||
                (stem.Length == 4 &&
                    (stem.StartsWith("COM", StringComparison.Ordinal) ||
                     stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    stem[3] >= '1' && stem[3] <= '9')) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                    return false;
            }
            return true;
        }

        private static void EnsurePortableDesktopStopped(string portableRoot,
            string rootToken)
        {
            IntPtr job = IntPtr.Zero;
            try
            {
                job = OpenJobObject(JobObjectQuery, false,
                    "Global\\LFPortable-DesktopJob-" + rootToken);
                if (job != IntPtr.Zero)
                    throw new IOException("Portable upgrade is blocked while Codex Desktop is running.");
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorFileNotFound)
                    throw new Win32Exception(error,
                        "Unable to verify whether the portable Codex Desktop is running.");
            }
            finally
            {
                if (job != IntPtr.Zero) CloseHandle(job);
            }

            string[] executableRoots = new string[] {
                Path.Combine(portableRoot, "CodexData", "app"),
                Path.Combine(portableRoot, "CodexData", "tools", "desktop-payloads")
            };
            for (int i = 0; i < executableRoots.Length; i++)
                executableRoots[i] = NormalizeDirectoryPath(executableRoots[i]);
            int currentProcessId = Process.GetCurrentProcess().Id;
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Exception ex)
            {
                throw new IOException("Unable to inspect portable desktop processes.", ex);
            }
            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    Process process = processes[i];
                    try
                    {
                        if (process.Id == currentProcessId) continue;
                        string executable;
                        if (TryGetExecutablePath(process, out executable))
                        {
                            string actual = NormalizeDirectoryPath(executable);
                            for (int c = 0; c < executableRoots.Length; c++)
                                if (IsSameOrDescendant(actual, executableRoots[c]))
                                    throw new IOException("Portable upgrade is blocked while Codex Desktop is running.");
                        }
                        else if (string.Equals(process.ProcessName, "CodexDesktop",
                            StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Unable to identify a Codex Desktop process safely.");
                    }
                    catch (IOException) { throw; }
                    catch { }
                }
            }
            finally
            {
                // Dispose any process handles that were not reached after a
                // fail-closed process check.
                for (int i = 0; i < processes.Length; i++)
                {
                    try { processes[i].Dispose(); } catch { }
                }
            }
        }

        private static bool IsSameOrDescendant(string candidate, string root)
        {
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
                return true;
            return candidate.StartsWith(DirectoryPrefix(root),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetExecutablePath(Process process, out string executable)
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
                handle = OpenProcess(ProcessQueryLimitedInformation, false,
                    unchecked((uint)process.Id));
                if (handle == IntPtr.Zero) return false;
                uint length = 32768;
                StringBuilder buffer = new StringBuilder((int)length);
                if (!QueryFullProcessImageName(handle, 0, buffer, ref length) ||
                    length == 0) return false;
                executable = buffer.ToString();
                return !string.IsNullOrEmpty(executable);
            }
            catch { return false; }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }

        private const uint MoveFileReplaceExisting = 0x00000001U;
        private const uint MoveFileWriteThrough = 0x00000008U;

        private const long LargeCommitStreamThreshold = 32L * 1024L * 1024L;

        private static bool StagedInputMatchesExisting(string staged, string existing)
        {
            // Launcher-only version bumps ship the very same payload packages.
            // Rewriting ~1.5 GB of identical data on a slow stick for every
            // micro release is what made "正在更新程序文件" sit for minutes
            // (and risk device stalls).  Compare a handful of samples instead;
            // any difference falls back to the full streaming commit.
            try
            {
                FileInfo stagedInfo = new FileInfo(staged);
                FileInfo existingInfo = new FileInfo(existing);
                if (stagedInfo.Length != existingInfo.Length || stagedInfo.Length <= 0)
                    return false;
                long length = stagedInfo.Length;
                long[] offsets = new long[] {
                    0, length / 4, length / 2, length - length / 4, length - 4096
                };
                if (offsets[4] < 0) offsets[4] = 0;
                byte[] stagedBuffer = new byte[4096];
                byte[] existingBuffer = new byte[4096];
                using (FileStream stagedStream = new FileStream(staged, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                using (FileStream existingStream = new FileStream(existing, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                {
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        long position = Math.Min(offsets[i], length - 4096);
                        stagedStream.Position = position;
                        existingStream.Position = position;
                        int stagedRead = ReadFully(stagedStream, stagedBuffer, 4096);
                        int existingRead = ReadFully(existingStream, existingBuffer, 4096);
                        if (stagedRead != existingRead) return false;
                        for (int j = 0; j < stagedRead; j++)
                            if (stagedBuffer[j] != existingBuffer[j]) return false;
                    }
                }
                return true;
            }
            catch
            {
                // Any read hiccup simply falls back to the streaming commit.
                return false;
            }
        }

        private static int ReadFully(FileStream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }


        private static void CommitStagedLargeFile(string source, string destination,
            PayloadProgressForm progress)
        {
            FileInfo sourceInfo = new FileInfo(source);
            if (PathExists(destination))
            {
                FileAttributes attributes = File.GetAttributes(destination);
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new IOException("Atomic input destination is a directory: " + destination);
                File.Delete(destination);
            }
            byte[] buffer = new byte[CopyBufferSize];
            try
            {
                using (FileStream input = new FileStream(source, FileMode.Open,
                    FileAccess.Read, FileShare.Read, CopyBufferSize,
                    FileOptions.SequentialScan))
                using (FileStream output = new FileStream(destination, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, CopyBufferSize,
                    FileOptions.SequentialScan))
                {
                    long written = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        output.Write(buffer, 0, read);
                        written = checked(written + read);
                        if (progress != null)
                            progress.PulseCopy(written, sourceInfo.Length);
                    }
                    // Never fsync the payload write (see above); the
                    // staged/committed length check guards completeness.
                    output.Flush();
                    if (written != sourceInfo.Length)
                        throw new InvalidDataException(
                            "The staged input ended unexpectedly: " + source);
                }
            }
            finally { Array.Clear(buffer, 0, buffer.Length); }
            File.Delete(source);
        }

        private static void MoveAtomically(string source, string destination,
            bool replaceExisting)
        {
            // Staged release files are already flushed while they are written.
            // A second MOVEFILE_WRITE_THROUGH forces a whole-file durable flush
            // on every update and can stall the UI for minutes on removable
            // media.  Keep the atomic rename; the staged write is durable.
            uint flags = replaceExisting ? MoveFileReplaceExisting : 0U;
            if (!MoveFileEx(source, destination, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Atomic portable input replacement failed: " + destination);
        }

        private static string NormalizeArchivePath(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized.Substring(2);
            normalized = normalized.TrimEnd('/');
            if (normalized.Length == 0) return "";
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.IndexOf(':') >= 0)
                throw new InvalidDataException(
                    "The embedded payload contains an absolute path.");
            string[] segments = normalized.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 0 || segment == "." || segment == ".." ||
                    segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal) ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException(
                        "The embedded payload contains an unsafe path.");
            }
            return normalized;
        }

        private static bool IsAllowedReleasePath(string path, bool directory,
            string architecture)
        {
            if (directory)
            {
                return string.Equals(path, "CodexData", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "CodexData/tools", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "CodexData/tools/launchers", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "CodexData/packages", StringComparison.OrdinalIgnoreCase);
            }

            if (path.StartsWith("CodexData/tools/launchers/CodexPortable.",
                StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf('/', "CodexData/tools/launchers/".Length) < 0)
                return true;
            if (path.StartsWith("CodexData/packages/LFPortable-",
                StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf('/', "CodexData/packages/".Length) < 0)
            {
                if (string.Equals(path, "CodexData/packages/LFPortable-common.zip",
                    StringComparison.OrdinalIgnoreCase)) return true;
                return string.Equals(path, "CodexData/packages/LFPortable-" +
                    architecture + ".msix", StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(path, "CodexData/README.txt",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "CodexData/THIRD_PARTY.txt",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectory(ZipArchiveEntry entry)
        {
            return string.IsNullOrEmpty(entry.Name) ||
                entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal);
        }

        private static void AssertArchiveEntryType(ZipArchiveEntry entry, bool directory)
        {
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            uint unixType = (attributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (attributes & 0x400) != 0)
                throw new InvalidDataException(
                    "The embedded payload contains a link or reparse point.");
            if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                throw new InvalidDataException(
                    "The embedded payload contains an unsupported entry type.");
            if (directory && unixType == 0x8000)
                throw new InvalidDataException(
                    "The embedded payload directory has a file type.");
            if (!directory && unixType == 0x4000)
                throw new InvalidDataException(
                    "The embedded payload file has a directory type.");
        }

        private static string ResolveTargetPath(string portableRoot, string relative)
        {
            string root = NormalizeDirectoryPath(portableRoot);
            string prefix = DirectoryPrefix(root);
            string target = Path.GetFullPath(Path.Combine(root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The embedded payload path escapes the portable directory.");
            return target;
        }

        private static void EnsureSafeDirectory(string root, string directory)
        {
            string fullRoot = NormalizeDirectoryPath(root);
            string fullDirectory = NormalizeDirectoryPath(directory);
            string prefix = DirectoryPrefix(fullRoot);
            if (!string.Equals(fullDirectory, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !fullDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Portable directory preparation escaped its root.");

            AssertExistingDirectoryIsSafe(fullRoot);
            if (string.Equals(fullDirectory, fullRoot, StringComparison.OrdinalIgnoreCase)) return;
            string relative = fullDirectory.Substring(prefix.Length);
            string[] segments = relative.Split('\\');
            string current = fullRoot;
            for (int i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                if (!Directory.Exists(current))
                {
                    if (PathExists(current))
                        throw new IOException(
                            "A file blocks portable directory preparation: " + current);
                    Directory.CreateDirectory(current);
                }
                AssertExistingDirectoryIsSafe(current);
            }
        }

        private static void AssertExistingDirectoryIsSafe(string directory)
        {
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(
                    "Portable program preparation refuses a linked directory: " + directory);
        }

        private static void AssertPortableRootAncestry(string portableRoot)
        {
            string fullRoot = NormalizeDirectoryPath(portableRoot);
            string volumeRoot = Path.GetPathRoot(fullRoot);
            if (string.IsNullOrEmpty(volumeRoot))
                throw new IOException("Portable root volume is unavailable.");
            string current = fullRoot;
            while (true)
            {
                AssertExistingDirectoryIsSafe(current);
                if (string.Equals(current, volumeRoot,
                    StringComparison.OrdinalIgnoreCase)) return;
                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = NormalizeDirectoryPath(parent);
            }
            throw new IOException("Portable root ancestry could not be verified.");
        }

        private static void AssertSafeExistingAncestry(string root, string directory)
        {
            string fullRoot = NormalizeDirectoryPath(root);
            string current = NormalizeDirectoryPath(directory);
            string prefix = DirectoryPrefix(fullRoot);
            if (!string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Portable program input escaped its root.");
            while (current.Length >= fullRoot.Length)
            {
                if (Directory.Exists(current) || PathExists(current))
                    AssertExistingDirectoryIsSafe(current);
                if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)) return;
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) break;
            }
            throw new IOException("Portable program input escaped its root.");
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string full = Path.GetFullPath(path);
            string volumeRoot = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(volumeRoot) && string.Equals(full, volumeRoot,
                StringComparison.OrdinalIgnoreCase)) return volumeRoot;
            return full.TrimEnd('\\');
        }

        private static string DirectoryPrefix(string root)
        {
            return root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
        }

        private static void AssertExistingPathIsRegularFile(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException(
                    "Portable program preparation refuses a linked or non-file path: " + path);
        }

        private static bool PathExists(string path)
        {
            string nativePath = ToExtendedPath(path);
            uint attributes = GetFileAttributes(nativePath);
            if (attributes != InvalidFileAttributes) return true;
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound || error == ErrorPathNotFound)
            {
                WIN32_FIND_DATA data;
                IntPtr find = FindFirstFile(nativePath, out data);
                if (find != InvalidHandleValue)
                {
                    FindClose(find);
                    return true;
                }
                int findError = Marshal.GetLastWin32Error();
                if (findError == ErrorFileNotFound || findError == ErrorPathNotFound)
                    return false;
                throw new Win32Exception(findError,
                    "Unable to inspect portable program path: " + path);
            }
            throw new Win32Exception(error,
                "Unable to inspect portable program path: " + path);
        }

        private static void CleanupStaleExtractionDirectories(string portableRoot)
        {
            string[] stale = Directory.GetDirectories(portableRoot,
                ".CodexData.extracting-*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < stale.Length; i++)
            {
                AssertExistingDirectoryIsSafe(stale[i]);
                ScheduleStagingCleanup(stale[i]);
            }
        }

        private static void DeleteDirectoryTree(string path)
        {
            string full = NormalizeDirectoryPath(path);
            if (!PathExists(full)) return;
            AssertExistingDirectoryIsSafe(full);
            DeleteDirectoryLongPath(ToExtendedPath(full));
        }

        private static string ToExtendedPath(string path)
        {
            string full = Path.GetFullPath(path).Replace('/', '\\');
            if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal))
                return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }

        private static void DeleteDirectoryLongPath(string directory)
        {
            WIN32_FIND_DATA data;
            IntPtr find = FindFirstFile(directory + "\\*", out data);
            if (find != InvalidHandleValue)
            {
                try
                {
                    bool more = true;
                    while (more)
                    {
                        string name = data.FileName;
                        if (name != "." && name != "..")
                        {
                            string child = directory + "\\" + name;
                            bool childDirectory =
                                (data.FileAttributes & FileAttributes.Directory) != 0;
                            bool reparse =
                                (data.FileAttributes & FileAttributes.ReparsePoint) != 0;
                            if (childDirectory && !reparse)
                                DeleteDirectoryLongPath(child);
                            else
                            {
                                SetFileAttributes(child, FileAttributes.Normal);
                                bool removed = childDirectory ?
                                    RemoveDirectory(child) : DeleteFile(child);
                                if (!removed) ThrowDeleteError(child);
                            }
                        }
                        more = FindNextFile(find, out data);
                        if (!more)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != ErrorNoMoreFiles)
                                throw new Win32Exception(error,
                                    "Portable temporary directory enumeration failed.");
                        }
                    }
                }
                finally { FindClose(find); }
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorFileNotFound && error != ErrorPathNotFound)
                    throw new Win32Exception(error,
                        "Portable temporary directory enumeration failed.");
            }
            SetFileAttributes(directory, FileAttributes.Normal);
            if (!RemoveDirectory(directory)) ThrowDeleteError(directory);
        }

        private static void ThrowDeleteError(string path)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorFileNotFound && error != ErrorPathNotFound)
                throw new Win32Exception(error,
                    "Portable temporary path could not be removed: " + path);
        }

        private static void EnsureFreeSpace(string root, long requiredBytes)
        {
            string volumeRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(volumeRoot)) return;
            DriveInfo drive = new DriveInfo(volumeRoot);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes + FreeSpaceReserve)
                throw new IOException(
                    "There is not enough free space to prepare Codex Portable beside this executable.");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName,
            string newFileName, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenJobObject(uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr processHandle,
            uint flags, StringBuilder executablePath, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            internal FileAttributes FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint Reserved0;
            internal uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            internal string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string fileName,
            out WIN32_FIND_DATA findFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr findFile);

        private const int ErrorNoMoreFiles = 18;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFile(IntPtr findFile,
            out WIN32_FIND_DATA findFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveDirectory(string pathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileAttributes(string fileName,
            FileAttributes fileAttributes);
    }

    internal sealed class ReadOnlySegmentStream : Stream
    {
        private readonly Stream source;
        private readonly long start;
        private readonly long length;
        private long position;

        internal ReadOnlySegmentStream(Stream source, long start, long length)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!source.CanRead || !source.CanSeek)
                throw new ArgumentException("Source stream must be readable and seekable.",
                    "source");
            if (start < 0 || length < 0 || start > source.Length - length)
                throw new ArgumentOutOfRangeException("length");
            this.source = source;
            this.start = start;
            this.length = length;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position
        {
            get { return position; }
            set
            {
                if (value < 0 || value > length)
                    throw new ArgumentOutOfRangeException("value");
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException("offset");
            if (position >= length) return 0;
            int allowed = (int)Math.Min((long)count, length - position);
            source.Position = start + position;
            int read = source.Read(buffer, offset, allowed);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next;
            switch (origin)
            {
                case SeekOrigin.Begin: next = offset; break;
                case SeekOrigin.Current: next = checked(position + offset); break;
                case SeekOrigin.End: next = checked(length + offset); break;
                default: throw new ArgumentOutOfRangeException("origin");
            }
            Position = next;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    internal sealed class PayloadProgressForm : Form
    {
        private readonly Label status;
        private readonly Label details;
        private readonly ProgressBar progress;
        private readonly Stopwatch uiUpdate = Stopwatch.StartNew();

        internal PayloadProgressForm()
        {
            Text = "Codex Portable";
            ClientSize = new System.Drawing.Size(440, 118);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            status = new Label();
            status.AutoSize = false;
            status.Location = new System.Drawing.Point(18, 16);
            status.Size = new System.Drawing.Size(404, 24);
            status.Text = "正在准备 Codex Portable / Preparing Codex Portable";

            progress = new ProgressBar();
            progress.Location = new System.Drawing.Point(18, 46);
            progress.Size = new System.Drawing.Size(404, 20);
            progress.Minimum = 0;
            progress.Maximum = 1000;

            details = new Label();
            details.AutoSize = false;
            details.Location = new System.Drawing.Point(18, 76);
            details.Size = new System.Drawing.Size(404, 22);

            Controls.Add(status);
            Controls.Add(progress);
            Controls.Add(details);
        }

        internal void Pump()
        {
            if (InvokeRequired) return;
            Refresh();
            Application.DoEvents();
        }

        internal void UpdateStatus(string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(UpdateStatus), text); }
                catch (InvalidOperationException) { }
                return;
            }
            status.Text = text ?? status.Text;
            Pump();
        }

        internal void PulseCopy(long completedBytes, long totalBytes)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<long, long>(PulseCopy), completedBytes,
                        totalBytes);
                }
                catch (InvalidOperationException) { }
                return;
            }
            if (uiUpdate.ElapsedMilliseconds < 100) return;
            uiUpdate.Restart();
            details.Text = string.Format(CultureInfo.InvariantCulture,
                "{0:0.0} / {1:0.0} MB  ·  " + status.Text, completedBytes / 1048576.0,
                totalBytes / 1048576.0);
            Pump();
        }

        internal void UpdateProgress(long completedBytes, long totalBytes,
            int completedFiles, int totalFiles)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<long, long, int, int>(UpdateProgress),
                        completedBytes, totalBytes, completedFiles, totalFiles);
                }
                catch (InvalidOperationException) { }
                return;
            }
            if (completedBytes < totalBytes && uiUpdate.ElapsedMilliseconds < 100) return;
            uiUpdate.Restart();
            int value = totalBytes <= 0 ? 0 :
                (int)Math.Min(1000L, completedBytes * 1000L / totalBytes);
            progress.Value = value;
            details.Text = string.Format(CultureInfo.InvariantCulture,
                "{0:0.0} / {1:0.0} MB    {2} / {3}",
                completedBytes / 1048576.0, totalBytes / 1048576.0,
                completedFiles, totalFiles);
            Refresh();
            Application.DoEvents();
        }
    }

    internal static class PortableRootIdentity
    {
        internal static string GetExecutionRootToken(string portableRoot)
        {
            if (string.IsNullOrEmpty(portableRoot)) throw new ArgumentException("portableRoot");
            string fullRoot = Path.GetFullPath(portableRoot).ToUpperInvariant();
            string volumeRoot = Path.GetPathRoot(fullRoot);
            uint serial;
            uint maximumComponentLength;
            uint flags;
            if (string.IsNullOrEmpty(volumeRoot) || !GetVolumeInformation(volumeRoot,
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformation(string rootPathName,
            StringBuilder volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber,
            out uint maximumComponentLength, out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer, uint fileSystemNameSize);
    }
}
