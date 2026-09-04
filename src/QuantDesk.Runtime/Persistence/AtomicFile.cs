namespace QuantDesk.Runtime.Persistence;

/// <summary>
/// Writes a file so a reader never observes a partial one.
///
/// Content is written to a uniquely named temporary file in the destination directory and then
/// moved over the destination, which is atomic on a single volume. A crash mid-write leaves either
/// the previous complete file or an orphaned temporary — never a truncated record.
///
/// The temporary name includes a GUID so two writers targeting the same path cannot corrupt each
/// other's temporary file. The move itself still resolves last-writer-wins; callers needing
/// stronger ordering must serialise above this type.
///
/// This existed as six near-identical private helpers across the persistence stores and the
/// dataset exporters. Durability is exactly the kind of knowledge that must have one authority:
/// a subtle divergence in one copy is invisible until a crash exposes it.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllBytesAsync(
        string path, byte[] contents, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string temporary = TemporaryPathFor(path);
        try
        {
            await File.WriteAllBytesAsync(temporary, contents, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string temporary = TemporaryPathFor(path);
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Writes through a caller-supplied stream action, for serializers that write incrementally.
    /// </summary>
    public static async Task WriteAsync(
        string path, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        string temporary = TemporaryPathFor(path);
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await write(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static string TemporaryPathFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// Best-effort cleanup. A leftover temporary is harmless — it is never read — so a failure to
    /// remove it must not mask the original write failure.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
