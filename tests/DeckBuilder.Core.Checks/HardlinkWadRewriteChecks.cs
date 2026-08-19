using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeckBuilder.GameData;
using Gibbed.Duels.FileFormats;

internal static class HardlinkWadRewriteChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Run();
        Console.WriteLine("PASS: hardlink WAD rewrite integrity");
    }

    private static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-hardlink-wad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source", "DATA_ALL_PLATFORMS", "FUNCTIONS");
            string myDecks = Path.Combine(root, "MyDecks");
            string gameRoot = Path.Combine(root, "Game");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(myDecks);
            Directory.CreateDirectory(gameRoot);

            string payload = Path.Combine(source, "TEST.LOL");
            File.WriteAllText(payload, "function TEST() return 1 end");

            string output = Path.Combine(myDecks, "Data_DLC_8000_Test_Runtime.wad");
            string active = Path.Combine(gameRoot, Path.GetFileName(output));
            UnpackedContentWadBuilder builder = new();

            builder.Build(
                new UnpackedContentBuildOptions(
                    Path.Combine(root, "source"),
                    output,
                    UnpackedContentKind.PortableCards,
                    Order: 100),
                default);

            CreateHardLink(active, output);
            ValidateNonZeroWad(output);
            ValidateSameFileBytes(output, active);

            File.WriteAllText(payload, "function TEST() return 2 end");
            builder.Build(
                new UnpackedContentBuildOptions(
                    Path.Combine(root, "source"),
                    output,
                    UnpackedContentKind.PortableCards,
                    Order: 101),
                default);

            ValidateNonZeroWad(output);
            ValidateNonZeroWad(active);
            ValidateSameFileBytes(output, active);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateNonZeroWad(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length == 0 || data.All(value => value == 0))
            throw new InvalidOperationException($"WAD was empty/all-zero after hardlink rewrite: {path}");

        using FileStream input = File.OpenRead(path);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
            throw new InvalidOperationException($"WAD header became invalid after hardlink rewrite: {reason}");
    }

    private static void ValidateSameFileBytes(string first, string second)
    {
        byte[] a = File.ReadAllBytes(first);
        byte[] b = File.ReadAllBytes(second);
        if (!a.AsSpan().SequenceEqual(b))
            throw new InvalidOperationException("Hardlink names no longer expose identical WAD bytes.");
    }

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
            throw new IOException($"CreateHardLinkW failed with Win32 error {Marshal.GetLastWin32Error()}.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
