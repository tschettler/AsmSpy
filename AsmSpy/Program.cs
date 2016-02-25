using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AsmSpy.Native;

namespace AsmSpy
{
    using System.Globalization;

    public class Program
    {
        private static readonly ConsoleColor[] ConsoleColors =
            {
                ConsoleColor.Green, 
                ConsoleColor.Red,
                ConsoleColor.Yellow, 
                ConsoleColor.Blue,
                ConsoleColor.Cyan, 
                ConsoleColor.Magenta,
            };

        private const string BindingRedirect = @"      <dependentAssembly>
        <assemblyIdentity name=""{0}"" publicKeyToken=""{1}"" culture=""{2}"" />
        <bindingRedirect oldVersion=""0.0.0.0-{3}"" newVersion=""{4}"" />
      </dependentAssembly>";

        private const string AssemblyBindingStart = @"  <runtime>
    <assemblyBinding xmlns=""urn:schemas-microsoft-com:asm.v1"">";

        private const string AssemblyBindingEnd = @"    </assemblyBinding>
  </runtime>";

        public static void Main(string[] args)
        {
            var firstArg = args.FirstOrDefault() ?? string.Empty;
            if (firstArg.EndsWith("help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return;
            }

            var directoryPath = args.FirstOrDefault();

            if (!Path.IsPathRooted(directoryPath))
            {
                directoryPath = Directory.GetCurrentDirectory();
            }

            if (!Directory.Exists(directoryPath))
            {
                PrintDirectoryNotFound(directoryPath);
                return;
            }


            var onlyConflicts = !args.Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase));  // args.Length != 2 || (args[1] != "all");
            var skipSystem = args.Any(x => x.Equals("nonsystem", StringComparison.OrdinalIgnoreCase));
            var bindingRedirects = args.Any(x => x.Equals("bindingredirects", StringComparison.OrdinalIgnoreCase));

            AnalyseAssemblies(new DirectoryInfo(directoryPath), onlyConflicts, skipSystem, bindingRedirects);
        }

        public static void AnalyseAssemblies(DirectoryInfo directoryInfo, bool onlyConflicts, bool skipSystem, bool bindingRedirects = false)
        {
            var assemblyFiles = directoryInfo.GetFiles("*.dll").Concat(directoryInfo.GetFiles("*.exe")).ToList();
            if (!assemblyFiles.Any())
            {
                Console.WriteLine("No dll files found in directory: '{0}'", directoryInfo.FullName);

                var binDir = directoryInfo.GetDirectories("bin", SearchOption.AllDirectories).FirstOrDefault();
                if (binDir == null)
                {
                    return;
                }

                AnalyseAssemblies(binDir, onlyConflicts, skipSystem, bindingRedirects);

                return;
            }

            Console.WriteLine("Check assemblies in:");
            Console.WriteLine(directoryInfo.FullName);
            Console.WriteLine(string.Empty);

            var binAssemblies = new List<Assembly>();
            var bindingRedirectsList = new List<string>();

            var assemblies = new Dictionary<string, IList<ReferencedAssembly>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fileInfo in assemblyFiles.OrderBy(asm => asm.Name))
            {
                Assembly assembly;
                try
                {
                    if (!fileInfo.IsAssembly())
                    {
                        continue;
                    }

                    assembly = Assembly.ReflectionOnlyLoadFrom(fileInfo.FullName);
                    binAssemblies.Add(assembly);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to load assembly '{0}': {1}", fileInfo.FullName, ex.Message);
                    continue;
                }

                foreach (var referencedAssembly in assembly.GetReferencedAssemblies())
                {
                    if (!assemblies.ContainsKey(referencedAssembly.Name))
                    {
                        assemblies.Add(referencedAssembly.Name, new List<ReferencedAssembly>());
                    }
                    assemblies[referencedAssembly.Name]
                        .Add(new ReferencedAssembly(referencedAssembly.Version, assembly));
                }
            }

            if (onlyConflicts)
            {
                Console.WriteLine("Detailing only conflicting assembly references.");
            }

            foreach (var assemblyReferences in assemblies.OrderBy(i => i.Key))
            {
                if (skipSystem
                    && (assemblyReferences.Key.StartsWith("System") || assemblyReferences.Key.StartsWith("mscorlib")))
                {
                    continue;
                }

                if (onlyConflicts && (assemblyReferences.Value.GroupBy(x => x.VersionReferenced).Count() == 1))
                {
                    continue;
                }

                var binAssembly = binAssemblies.FirstOrDefault(a => a.GetName().Name == assemblyReferences.Key);
                var binVersion = binAssembly == null ? null : binAssembly.GetName().Version;

                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("Reference: ");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("{0}", assemblyReferences.Key);

                if (binVersion != null)
                {
                    Console.WriteLine("Bin version: {0}", binAssembly.GetName().Version);
                }

                var referencedAssemblies = new List<Tuple<string, string>>();
                var versionsList = new List<string>();
                var asmList = new List<string>();
                foreach (var referencedAssembly in assemblyReferences.Value)
                {
                    var s1 = referencedAssembly.VersionReferenced.ToString();
                    var s2 = referencedAssembly.ReferencedBy.GetName().Name;
                    var tuple = new Tuple<string, string>(s1, s2);
                    referencedAssemblies.Add(tuple);
                }

                foreach (var referencedAssembly in referencedAssemblies)
                {
                    if (!versionsList.Contains(referencedAssembly.Item1))
                    {
                        versionsList.Add(referencedAssembly.Item1);
                    }
                    if (!asmList.Contains(referencedAssembly.Item1))
                    {
                        asmList.Add(referencedAssembly.Item1);
                    }
                }

                // order the versions
                versionsList = versionsList.OrderByDescending(v => new Version(v)).ToList();

                if (binVersion != null)
                {
                    // move the bin version to the top
                    versionsList.Remove(binVersion.ToString());
                    versionsList.Insert(0, binVersion.ToString());
                }

                foreach (var referencedAssembly in referencedAssemblies)
                {
                    var versionColor = ConsoleColors[versionsList.IndexOf(referencedAssembly.Item1) % ConsoleColors.Length];

                    Console.ForegroundColor = versionColor;
                    Console.Write("   {0}", referencedAssembly.Item1);

                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write(" by ");

                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine("{0}", referencedAssembly.Item2);
                }

                Console.WriteLine();

                if (!bindingRedirects || binVersion == null)
                {
                    continue;
                }

                var name = binAssembly.GetName();
                var maxVersion = versionsList.Max(v => new Version(v));
                var publicKeyToken = GetPublicKeyTokenFromAssembly(binAssembly);
                var culture = name.CultureInfo.Name;
                culture = string.IsNullOrWhiteSpace(culture) ? "neutral" : culture;

                bindingRedirectsList.Add(string.Format(BindingRedirect, name.Name, publicKeyToken, culture, maxVersion, binVersion));
            }

            if (!bindingRedirects)
            {
                return;
            }

            Console.WriteLine("Assembly binding redirects to add to config file:");
            Console.WriteLine(AssemblyBindingStart);

            foreach (var bindingRedirectEntry in bindingRedirectsList)
            {
                Console.WriteLine(bindingRedirectEntry);
            }

            Console.WriteLine(AssemblyBindingEnd);
        }

        private static string GetPublicKeyTokenFromAssembly(Assembly assembly)
        {
            var bytes = assembly.GetName().GetPublicKeyToken();
            if (bytes == null || bytes.Length == 0)
            {
                return "null";
            }

            var publicKeyToken = string.Empty;
            for (var i = 0; i < bytes.GetLength(0); i++)
            {
                publicKeyToken += string.Format("{0:x2}", bytes[i]);
            }

            return publicKeyToken;
        }

        private static void PrintDirectoryNotFound(string directoryPath)
        {
            Console.WriteLine("Directory: '" + directoryPath + "' does not exist.");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("AsmSpy [directory to load assemblies from] [all] [nonsystem] [bindingredirects]");
            Console.WriteLine("E.g.");
            Console.WriteLine(@"AsmSpy C:\Source\My.Solution\My.Project\bin\Debug");
            Console.WriteLine(@"AsmSpy C:\Source\My.Solution\My.Project\bin\Debug all");
            Console.WriteLine(@"AsmSpy all nonsystem");
            Console.WriteLine(@"AsmSpy bindingredirects");
        }
    }

    public class ReferencedAssembly
    {
        public Version VersionReferenced { get; private set; }
        public Assembly ReferencedBy { get; private set; }

        public ReferencedAssembly(Version versionReferenced, Assembly referencedBy)
        {
            VersionReferenced = versionReferenced;
            ReferencedBy = referencedBy;
        }
    }
}
