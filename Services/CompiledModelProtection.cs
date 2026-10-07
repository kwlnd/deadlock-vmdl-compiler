using System.Security.Cryptography;

namespace DeadlockVmdlCompiler.Services;

/// <summary>
/// Allows readers such as the VPK packer, but prevents CSDK12 from replacing a deployed model.
/// The operating system releases the handle if the application exits unexpectedly.
/// </summary>
public sealed class CompiledModelProtection : IDisposable
{
    private FileStream? _handle;

    public string ModelPath { get; }

    private CompiledModelProtection(string modelPath, FileStream handle)
    {
        ModelPath = modelPath;
        _handle = handle;
    }

    public static CompiledModelProtection Acquire(string deployedPath, string compilerOutputPath)
    {
        var handle = new FileStream(deployedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var compilerOutput = new FileStream(compilerOutputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (handle.Length != compilerOutput.Length ||
                !SHA256.HashData(handle).AsSpan().SequenceEqual(SHA256.HashData(compilerOutput)))
            {
                throw new InvalidDataException(
                    "The deployed model changed before it could be protected. Compile it again before packaging.");
            }

            handle.Position = 0;
            return new CompiledModelProtection(Path.GetFullPath(deployedPath), handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
