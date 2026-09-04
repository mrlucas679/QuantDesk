using System.Text;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Persistence;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"quantdesk-atomic-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_directory);

    private string Path_(string name) => Path.Combine(_directory, name);

    [Fact]
    public async Task WritingBytesProducesExactlyTheRequestedContent()
    {
        string path = Path_("payload.json");
        byte[] contents = Encoding.UTF8.GetBytes("""{"a":1}""");

        await AtomicFile.WriteAllBytesAsync(path, contents, CancellationToken.None);

        Assert.Equal(contents, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WritingOverAnExistingFileReplacesItCompletely()
    {
        string path = Path_("payload.json");
        await AtomicFile.WriteAllBytesAsync(
            path, Encoding.UTF8.GetBytes("a much longer previous value"), CancellationToken.None);

        await AtomicFile.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("short"), CancellationToken.None);

        // A non-atomic overwrite could leave trailing bytes of the longer previous value.
        Assert.Equal("short", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void WritingTextReplacesTheFileAtomically()
    {
        string path = Path_("snapshot.json");
        AtomicFile.WriteAllText(path, "first");

        AtomicFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public async Task AFailedStreamWriteLeavesThePreviousFileIntact()
    {
        string path = Path_("durable.json");
        await AtomicFile.WriteAllBytesAsync(
            path, Encoding.UTF8.GetBytes("original"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AtomicFile.WriteAsync(
            path,
            async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("partial"), token);
                throw new InvalidOperationException("serializer failed mid-write");
            },
            CancellationToken.None));

        // The destination must still hold the last complete value, never the partial one.
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AFailedWriteLeavesNoTemporaryFileBehind()
    {
        string path = Path_("durable.json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => AtomicFile.WriteAsync(
            path,
            (_, _) => throw new InvalidOperationException("failed immediately"),
            CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentWritersEachProduceACompleteFile()
    {
        string path = Path_("contended.json");
        string[] payloads = [.. Enumerable.Range(0, 20).Select(index => new string('x', 100 + index))];

        await Task.WhenAll(payloads.Select(payload =>
            AtomicFile.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(payload), CancellationToken.None)));

        // Last writer wins, but the survivor must be one of the payloads in full — never a blend.
        Assert.Contains(await File.ReadAllTextAsync(path), payloads);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task StreamWritesRoundTrip()
    {
        string path = Path_("streamed.json");

        await AtomicFile.WriteAsync(
            path,
            async (stream, token) => await stream.WriteAsync(Encoding.UTF8.GetBytes("streamed"), token),
            CancellationToken.None);

        Assert.Equal("streamed", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyPathIsRejected(string? path)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            AtomicFile.WriteAllBytesAsync(path!, [1], CancellationToken.None));
        Assert.ThrowsAny<ArgumentException>(() => AtomicFile.WriteAllText(path!, "x"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
