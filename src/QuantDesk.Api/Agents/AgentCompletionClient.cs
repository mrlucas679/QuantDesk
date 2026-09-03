using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using QuantDesk.Domain.Agents;

namespace QuantDesk.Api.Agents;

public interface IAgentCompletionClient
{
    Task<AgentCompletion> CompleteAsync(AgentInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Bounded OpenAI-compatible structured-output client. It offers the model no tools at all.
///
/// Two guards here could not previously fail, which is worse than not having them
/// -------------------------------------------------------------------------------
/// The forbidden-tool check filtered <c>invocation.AllowedTools</c> for a deny list -- but every
/// invocation is built with an empty tool set, so the filter ran over nothing every time. The real
/// invariant is not "no forbidden tool is offered", it is "no tool is offered", and that is what is
/// asserted now. A deny list also fails open on the tool nobody thought to name.
///
/// The response's tool calls were never read. <c>AgentCompletion</c> was constructed with an empty
/// list unconditionally, so <c>EnsureNoMutations</c> -- the check that an agent did not mutate
/// external state -- was reading a list this client guaranteed was empty. An OpenAI-compatible
/// provider can return <c>tool_calls</c>, and they were being dropped on the floor before the guard
/// that exists to notice them. They are parsed now, and any tool call at all is a violation,
/// because none was ever offered.
///
/// Neither was exploitable today: no tools are sent, so a well-behaved provider returns none. That
/// is exactly the problem with a check that cannot fail -- it reports safety derived from a
/// coincidence of the current configuration rather than from anything it verified.
/// </summary>
public sealed class AgentCompletionClient(HttpClient http, AgentRuntimeOptions options) : IAgentCompletionClient
{
    /// <summary>
    /// The chat-completions endpoint, however the operator wrote the base URL.
    ///
    /// This was <c>new Uri(baseUri, "v1/chat/completions")</c>, and relative resolution makes that
    /// quietly dependent on a trailing slash:
    ///
    /// <code>
    /// https://api.featherless.ai/     -> https://api.featherless.ai/v1/chat/completions
    /// https://api.featherless.ai/v1   -> https://api.featherless.ai/v1/chat/completions
    /// https://api.featherless.ai/v1/  -> https://api.featherless.ai/v1/v1/chat/completions
    /// </code>
    ///
    /// Two of the three forms work and the third 404s. The one that fails is the one a provider's
    /// own quickstart hands you -- Featherless documents <c>base_url="https://api.featherless.ai/v1"</c>
    /// -- because writing a URL ending in a path segment with a trailing slash is the natural thing
    /// to do. And a 404 from an inference provider reads as a bad key or an unavailable model, so
    /// the operator would go and check the credential that was never wrong.
    ///
    /// Built from the origin instead, with any version segment the operator supplied discarded,
    /// because the version this client speaks is a property of this client rather than of the
    /// configuration.
    /// </summary>
    internal static Uri CompletionsEndpoint(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        string path = baseUri.AbsolutePath.Trim('/');
        if (string.Equals(path, "v1", StringComparison.OrdinalIgnoreCase)) path = string.Empty;

        string prefix = path.Length == 0 ? string.Empty : path + "/";
        return new Uri($"{baseUri.GetLeftPart(UriPartial.Authority)}/{prefix}v1/chat/completions");
    }

    public async Task<AgentCompletion> CompleteAsync(
        AgentInvocation invocation, CancellationToken cancellationToken)
    {
        if (!options.Enabled || options.BaseUri is null)
            throw new InvalidOperationException("AGENT_PROVIDER_DISABLED");

        // No tools, rather than no forbidden tools. A deny list fails open on whatever nobody
        // thought to name, and these agents read evidence and return JSON -- there is no tool they
        // have any business being offered.
        if (invocation.AllowedTools.Count > 0)
            throw new InvalidOperationException("AGENT_TOOLS_ARE_NEVER_OFFERED");

        using var request = new HttpRequestMessage(HttpMethod.Post, CompletionsEndpoint(options.BaseUri));
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = options.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = invocation.SystemPrompt + "\nReturn only JSON matching: " + invocation.OutputContract },
                new { role = "user", content = invocation.InputJson }
            }
        });

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Bounded. The body comes from a process this one does not control, and an unbounded
        // JsonDocument.ParseAsync over a hostile or malfunctioning provider is a way to lose the
        // trading host to a reply.
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new BoundedReadStream(stream, MaximumResponseBytes);
        using JsonDocument body = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken);

        JsonElement root = body.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind is not JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            // Indexing choices[0] blind turned a provider returning nothing into an
            // IndexOutOfRangeException, which reads as a bug here rather than as a bad reply.
            throw new InvalidDataException("AGENT_RESPONSE_HAS_NO_CHOICES");
        }

        JsonElement message = choices[0].GetProperty("message");
        string? output = message.TryGetProperty("content", out JsonElement content)
            ? content.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidDataException("EMPTY_AGENT_OUTPUT");

        return new AgentCompletion(options.Model, output, ReadToolCalls(message));
    }

    /// <summary>
    /// The most a well-behaved structured reply can be. Beyond this the provider is malfunctioning
    /// or hostile, and either way the answer is to stop reading rather than to keep allocating.
    /// </summary>
    private const long MaximumResponseBytes = 1_048_576;

    /// <summary>
    /// Tool calls the provider returned, so the mutation guard has something real to inspect.
    ///
    /// Every one is reported as having mutated external state. This client offers no tools, so a
    /// tool call in the reply means the provider did something nobody asked for -- and whether it
    /// actually changed anything is not knowable from here. Treating it as a mutation is the only
    /// safe reading, and it makes EnsureNoMutations a check that can fail.
    /// </summary>
    private static IReadOnlyList<AgentToolCall> ReadToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out JsonElement calls)
            || calls.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var read = new List<AgentToolCall>();
        foreach (JsonElement call in calls.EnumerateArray())
        {
            string id = call.TryGetProperty("id", out JsonElement identifier)
                ? identifier.GetString() ?? "unnamed"
                : "unnamed";
            read.Add(new AgentToolCall(id, new Dictionary<string, string>(StringComparer.Ordinal),
                MutatedExternalState: true));
        }

        return read;
    }

    /// <summary>Reads at most a fixed number of bytes, then fails rather than continuing.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await inner.ReadAsync(buffer, cancellationToken);
            _read += count;
            if (_read > limit) throw new InvalidDataException("AGENT_RESPONSE_TOO_LARGE");
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            _read += read;
            if (_read > limit) throw new InvalidDataException("AGENT_RESPONSE_TOO_LARGE");
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
