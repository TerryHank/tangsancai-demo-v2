using System.Reflection;
using System.Runtime.Loader;

static class V2Runtime
{
    public static void Register()
    {
        AssemblyLoadContext.Default.Resolving += (context, assembly) =>
        {
            if (assembly.Name != "Microsoft.Kinect") return null;
            const string identity = "v4.0_2.0.0.0__31bf3856ad364e35";
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var gac = Path.Combine(windows, "Microsoft.NET", "assembly");
            foreach (var architecture in new[] { "GAC_64", "GAC_MSIL" })
            {
                var installed = Path.Combine(gac, architecture, "Microsoft.Kinect", identity, "Microsoft.Kinect.dll");
                if (File.Exists(installed)) return context.LoadFromAssemblyPath(installed);
            }
            var sdk = Environment.GetEnvironmentVariable("KINECTSDK20_DIR");
            if (!string.IsNullOrEmpty(sdk))
            {
                var installed = Path.Combine(sdk, "Assemblies", "Microsoft.Kinect.dll");
                if (File.Exists(installed)) return context.LoadFromAssemblyPath(installed);
            }
            return null;
        };
    }

    public static bool IsInstalled()
    {
        try
        {
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Microsoft.Kinect"));
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }
}
