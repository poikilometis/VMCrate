using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed class UdfInboxWriter : IInboxWriter
{
    public void Write(IReadOnlyList<string> sourceFiles, string stagingDirectory, string isoPath)
    {
        foreach (var source in sourceFiles)
            File.Copy(source, Path.Combine(stagingDirectory, Path.GetFileName(source)));

        try { WriteDirectory(stagingDirectory, isoPath, "VMCRATE_INBOX"); }
        catch (COMException error) { throw new LabException("error.inbox", error.Message, error); }
    }

    internal static void WriteDirectory(string stagingDirectory, string isoPath, string volume, string? efiImage = null)
    {
        dynamic? fileSystem = null;
        dynamic? imageRoot = null;
        dynamic? result = null;
        dynamic? boot = null;
        dynamic? bootStream = null;
        IStream? stream = null;
        try
        {
            fileSystem = CreateCom("IMAPI2FS.MsftFileSystemImage");
            fileSystem.FileSystemsToCreate = efiImage is null ? 4 : 7; // UDF, or ISO 9660 + Joliet + UDF.
            fileSystem.UDFRevision = 0x102;
            fileSystem.VolumeName = volume;
            fileSystem.FreeMediaBlocks = 0;
            if (efiImage is not null)
            {
                bootStream = CreateCom("ADODB.Stream");
                bootStream.Type = 1;
                bootStream.Open();
                bootStream.LoadFromFile(efiImage);
                boot = CreateCom("IMAPI2FS.BootOptions");
                boot.AssignBootImage(bootStream);
                boot.PlatformId = 0xef;
                boot.Emulation = 0;
                fileSystem.BootImageOptions = boot;
            }
            imageRoot = fileSystem.Root;
            imageRoot.AddTree(stagingDirectory, false);
            result = fileSystem.CreateResultImage();
            stream = (IStream)result.ImageStream;
            WriteImage(stream, isoPath);
        }
        finally
        {
            Release(stream);
            Release(result);
            Release(imageRoot);
            Release(fileSystem);
            Release(boot);
            Release(bootStream);
        }
    }

    private static object CreateCom(string name)
    {
        var type = Type.GetTypeFromProgID(name) ?? throw new COMException(name);
        return Activator.CreateInstance(type) ?? throw new COMException(name);
    }

    private static void WriteImage(IStream stream, string path)
    {
        const int chunkSize = 1024 * 1024;
        var buffer = new byte[chunkSize];
        var bytesRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            int count;
            do
            {
                Marshal.WriteInt32(bytesRead, 0);
                stream.Read(buffer, buffer.Length, bytesRead);
                count = Marshal.ReadInt32(bytesRead);
                output.Write(buffer, 0, count);
            } while (count > 0);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            Marshal.FreeHGlobal(bytesRead);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
