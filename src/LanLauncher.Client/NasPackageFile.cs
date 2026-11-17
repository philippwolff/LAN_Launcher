using System.IO;

namespace LanLauncher.Client;

/// <summary>Shared by the launcher and the standalone open-only probe.</summary>
internal static class NasPackageFile
{
    internal const int BufferSize = 128 * 1024;
    internal const FileOptions Options = FileOptions.Asynchronous;
    internal const string Parameters = "Mode=Open, Access=Read, Share=Read, Buffer=131072, Options=Asynchronous";

    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, Options);
}
