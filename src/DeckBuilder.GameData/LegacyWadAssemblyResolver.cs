using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;

namespace DeckBuilder.GameData;

internal static class LegacyWadAssemblyResolver
{
    private static readonly HashSet<string> SupportedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gibbed.Duels.FileFormats",
        "Gibbed.IO",
        "Gibbed.Squish",
        "ICSharpCode.SharpZipLib"
    };

#pragma warning disable CA2255 // Required before legacy WAD types are first resolved by .NET 8.
    [ModuleInitializer]
    internal static void Initialize()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AssemblyLoadContext.Default.Resolving += ResolveFromApplicationDirectory;
    }
#pragma warning restore CA2255

    private static Assembly? ResolveFromApplicationDirectory(
        AssemblyLoadContext context,
        AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || !SupportedAssemblies.Contains(assemblyName.Name))
        {
            return null;
        }

        string path = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
