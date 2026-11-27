using System.IO;

namespace DbConnect.Services;

/// <summary>Filesystem boundary for writing, atomic publication, and owned-file cleanup.</summary>
public interface IExportFileOperations
{
    Stream CreateTemporary(string path);
    void Publish(string temporaryPath, string destinationPath);
    void DeleteTemporary(string path);
}

public sealed class LocalExportFileOperations : IExportFileOperations
{
    public Stream CreateTemporary(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true);
    public void Publish(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath)) File.Replace(temporaryPath, destinationPath, null);
        else File.Move(temporaryPath, destinationPath);
    }
    public void DeleteTemporary(string path) => File.Delete(path);
}
