using RimContext.Core.Contracts;

namespace RimContext.Core.Storage;

internal sealed class StoreLock : IDisposable
{
    private readonly FileStream stream;

    private StoreLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static StoreLock Acquire(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            return new StoreLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            throw ErrorFactory.StoreLocked();
        }
        catch (UnauthorizedAccessException)
        {
            throw ErrorFactory.StoreLocked();
        }
    }

    public void Dispose() => stream.Dispose();
}
