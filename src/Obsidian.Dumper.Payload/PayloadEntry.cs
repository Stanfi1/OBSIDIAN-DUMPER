using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Payload
{
    /// <summary>
    /// Managed agent that executes *inside* the inspected process.
    ///
    /// Entry contract (both hosting paths):
    ///   public static int DumpAll(string outputDirectory)
    ///
    /// * .NET Framework  : ICLRRuntimeHost::ExecuteInDefaultAppDomain
    /// * .NET Core / 5+  : reserved (a live CoreCLR cannot host a second runtime,
    ///                     so the out-of-process engine is used instead)
    ///
    /// The agent is deliberately dependency free and reflection only. It never
    /// loads anything from the target's own load context.
    /// </summary>
    public static class PayloadEntry
    {
        private const string ManifestName = "obsidian-manifest.tsv";
        private const string LogName = "obsidian-payload.log";

        private const uint MemCommit = 0x1000;
        private const uint PageNoAccess = 0x01;
        private const uint PageGuard = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation information, IntPtr length);

        public static int DumpAll(string argument)
        {
            var log = new StringBuilder();
            int written = 0;

            try
            {
                var output = argument;
                if (string.IsNullOrEmpty(output))
                    return -2;

                Directory.CreateDirectory(output);

                var manifest = new StringBuilder();
                manifest.Append("#obsidian-manifest\t1\n");
                manifest.Append("#assembly\tmodule\tfile\tbytes\tsha256\tkind\tlocation\n");

                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                Array.Sort(assemblies, (a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));

                foreach (var assembly in assemblies)
                {
                    if (assembly == null)
                        continue;

                    string assemblyName = Safe(assembly.FullName);
                    bool isDynamic;
                    try { isDynamic = assembly.IsDynamic; }
                    catch { isDynamic = false; }

                    string location = string.Empty;
                    try { location = assembly.Location ?? string.Empty; }
                    catch { }

                    Module[] modules;
                    try { modules = assembly.GetModules(); }
                    catch { modules = new Module[0]; }

                    if (modules.Length == 0)
                    {
                        manifest.Append(Escape(assemblyName)).Append('\t').Append(Escape("<none>")).Append('\t')
                            .Append('\t').Append('0').Append('\t').Append('\t')
                            .Append(isDynamic ? "dynamic" : "unavailable").Append('\t')
                            .Append(Escape(location)).Append('\n');
                        continue;
                    }

                    foreach (var module in modules)
                    {
                        if (module == null)
                            continue;

                        string moduleName = Safe(module.Name);
                        byte[] image = null;
                        string kind = null;

                        if (!isDynamic)
                        {
                            image = TryReadMappedModule(module, log, moduleName);
                            if (image != null)
                                kind = "memory";
                        }

                        if (image == null && location.Length > 0 && File.Exists(location) && modules.Length == 1)
                        {
                            try
                            {
                                image = File.ReadAllBytes(location);
                                kind = "file";
                            }
                            catch (Exception ex)
                            {
                                log.Append("read-file failed ").Append(moduleName).Append(": ").Append(ex.Message).Append('\n');
                            }
                        }

                        if (image == null)
                        {
                            manifest.Append(Escape(assemblyName)).Append('\t').Append(Escape(moduleName)).Append('\t')
                                .Append('\t').Append('0').Append('\t').Append('\t')
                                .Append(isDynamic ? "dynamic" : "unreadable").Append('\t')
                                .Append(Escape(location)).Append('\n');
                            continue;
                        }

                        string fileName = UniqueFileName(usedNames, moduleName, image);
                        string path = Path.Combine(output, fileName);
                        File.WriteAllBytes(path, image);

                        string hash = Sha256(image);
                        manifest.Append(Escape(assemblyName)).Append('\t').Append(Escape(moduleName)).Append('\t')
                            .Append(Escape(fileName)).Append('\t').Append(image.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
                            .Append(hash).Append('\t').Append(kind).Append('\t')
                            .Append(Escape(location)).Append('\n');

                        written++;
                        log.Append("dumped ").Append(moduleName).Append(" -> ").Append(fileName)
                           .Append(" (").Append(image.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes, ").Append(kind).Append(")\n");
                    }
                }

                File.WriteAllText(Path.Combine(output, ManifestName), manifest.ToString(), new UTF8Encoding(false));
                log.Append("done: ").Append(written.ToString(CultureInfo.InvariantCulture)).Append(" module(s)\n");
                return written;
            }
            catch (Exception ex)
            {
                log.Append("fatal: ").Append(ex).Append('\n');
                return -1;
            }
            finally
            {
                try
                {
                    string directory = argument;
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        File.WriteAllText(Path.Combine(directory, LogName), log.ToString(), new UTF8Encoding(false));
                    }
                }
                catch
                {
                    // Diagnostics must never fail the dump.
                }
            }
        }

        private static byte[] TryReadMappedModule(Module module, StringBuilder log, string moduleName)
        {
            try
            {
                IntPtr handle = Marshal.GetHINSTANCE(module);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                    return null;

                if (Marshal.ReadInt16(handle) != PeImageLayout.DosSignature)
                    return null;

                int peOffset = Marshal.ReadInt32(handle, 0x3C);
                if (peOffset <= 0 || peOffset > 0x1000)
                    return null;
                if (Marshal.ReadInt32(handle, peOffset) != unchecked((int)PeImageLayout.NtSignature))
                    return null;

                int optional = peOffset + 24;
                int sizeOfImage = Marshal.ReadInt32(handle, optional + 56);
                if (sizeOfImage <= 0 || sizeOfImage > 512 * 1024 * 1024)
                    return null;

                byte[] mapped;
                if (!TryCopyMappedImage(handle, sizeOfImage, out mapped))
                    return null;

                var rebuilt = PeImageLayout.Rebuild(mapped, new PeRebuildOptions());
                return rebuilt ?? mapped;
            }
            catch (Exception ex)
            {
                log.Append("mapped-read failed ").Append(moduleName).Append(": ").Append(ex.Message).Append('\n');
                return null;
            }
        }

        /// <summary>
        /// Copies a loader-mapped image page by page. A plain Marshal.Copy over the
        /// whole image faults on reserved gaps (normal for .NET modules with 0x2000
        /// section alignment); an uncatchable access violation would take down the
        /// target process, so unreadable pages are simply left zero-filled.
        /// </summary>
        private static bool TryCopyMappedImage(IntPtr imageBase, int size, out byte[] mapped)
        {
            mapped = null;
            try
            {
                var buffer = new byte[size];
                long baseAddress = imageBase.ToInt64();
                int offset = 0;
                int querySize = Marshal.SizeOf(typeof(MemoryBasicInformation));

                while (offset < size)
                {
                    long address = baseAddress + offset;
                    MemoryBasicInformation info;
                    if (VirtualQuery((IntPtr)address, out info, (IntPtr)querySize) == IntPtr.Zero)
                        return false;

                    long regionSize = info.RegionSize.ToInt64();
                    long regionOffset = address - info.BaseAddress.ToInt64();
                    if (regionSize <= 0 || regionOffset < 0 || regionOffset >= regionSize)
                        return false;

                    int slice = (int)Math.Min(size - offset, regionSize - regionOffset);
                    bool readable = info.State == MemCommit
                                    && (info.Protect & PageNoAccess) == 0
                                    && (info.Protect & PageGuard) == 0;
                    if (readable)
                        Marshal.Copy((IntPtr)address, buffer, offset, slice);

                    offset += slice;
                }

                mapped = buffer;
                return true;
            }
            catch (Exception)
            {
                mapped = null;
                return false;
            }
        }

        private static string UniqueFileName(HashSet<string> used, string moduleName, byte[] image)
        {
            string extension = ".dll";
            try
            {
                PeHeaders headers;
                // 0x2000 = IMAGE_FILE_DLL; images without it are executables.
                if (PeImageLayout.TryReadHeaders(image, out headers) && (headers.Characteristics & 0x2000) == 0)
                    extension = ".exe";
            }
            catch
            {
                // keep .dll
            }

            string stem = Sanitize(moduleName);
            if (stem.Length == 0)
                stem = "module";
            if (!stem.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !stem.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                stem += extension;

            string candidate = stem;
            int index = 2;
            while (!used.Add(candidate))
            {
                candidate = Path.GetFileNameWithoutExtension(stem) + "_" + index.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(stem);
                index++;
            }
            return candidate;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = false;
                foreach (char v in invalid)
                {
                    if (c == v) { bad = true; break; }
                }
                builder.Append(bad || c == '\\' || c == '/' ? '_' : c);
            }
            return builder.ToString().Trim();
        }

        private static string Safe(string value) => value ?? string.Empty;

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }
    }
}
