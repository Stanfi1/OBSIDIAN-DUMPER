using System;
using System.IO;
using System.Reflection;

namespace ObsidianNet48Target
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("net48 target pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Console.WriteLine("framework=" + Environment.Version);
            Console.WriteLine(Secrets.Describe());

            // A byte[]-loaded assembly: invisible to the module list, only the
            // in-process agent can see it. The payload is embedded as a managed
            // assembly under the logical name "embedded.bin".
            byte[] payload = ReadEmbeddedPayload();
            if (payload != null)
            {
                try
                {
                    var assembly = Assembly.Load(payload);
                    Console.WriteLine("loaded hidden assembly: " + assembly.FullName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("hidden assembly failed: " + ex.Message);
                }
            }
            else
            {
                Console.WriteLine("hidden payload resource is not embedded in this build");
            }

            Console.WriteLine("ready");
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(600));
        }

        private static byte[] ReadEmbeddedPayload()
        {
            try
            {
                Assembly self = Assembly.GetExecutingAssembly();
                string selected = null;
                foreach (string name in self.GetManifestResourceNames())
                {
                    if (name.Equals("embedded.bin", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".embedded.bin", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("payload.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        selected = name;
                        break;
                    }
                }

                if (selected == null)
                    return null;

                using (Stream stream = self.GetManifestResourceStream(selected))
                {
                    if (stream == null)
                        return null;
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        return buffer.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }
    }

    public static class Secrets
    {
        public static string Describe()
        {
            return "OBSIDIAN-NET48-TARGET " + typeof(Secrets).FullName;
        }
    }
}
