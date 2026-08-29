using Microsoft.Extensions.AI;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// A stand-in for the language model, returning canned JSON.
///
/// <para>
/// This project's standing rule is to prefer an integration test through the real
/// surface over a unit test with a fake, because the bugs it actually has — SQL
/// that does not translate, delete behaviour, one rule enforced on one surface
/// only — are invisible to fakes. That rule is not being broken here; the
/// database, the HTTP surface, the GraphQL surface and the real Program.cs are
/// all live in these tests. Only the model is faked.
/// </para>
///
/// <para>
/// It is faked because it is the one dependency where the real thing would test
/// nothing. A language model is non-deterministic by construction, so an
/// assertion about its output is either vacuous ("something came back") or flaky.
/// What Phase 4 can actually get wrong is on this side of the boundary: parsing a
/// reply that does not match the schema, storing a second ai_analyses row on
/// re-run, restamping a human-entered skill as AI-extracted, or blowing up on a
/// duplicate the model emitted twice. Every one of those is deterministic, and
/// every one needs a *chosen* model response to provoke it.
/// </para>
///
/// <para>
/// The consequence, stated rather than left implicit: nothing in CI proves the
/// prompt gets good answers out of llama3.2:3b. That is checked by hand against a
/// real posting, and the phase doc records what came back.
/// </para>
/// </summary>
public sealed class FakeChatClient(string json) : IChatClient
{
    /// <summary>The prompt the analyzer actually sent, for tests that assert on it.</summary>
    public string? LastPrompt { get; private set; }

    /// <summary>
    /// When set, every call throws this instead of answering — the model server being
    /// unreachable.
    ///
    /// <para>
    /// Added in Phase 5, and it tests something the canned-reply mode cannot. The ATS
    /// check has four stages and only one of them needs a model, so an outage there is
    /// supposed to <em>degrade</em>: it returns the skill gap and the formatting notes
    /// with a warning rather than failing. That is a behavioural promise, and the only
    /// way to hold it to account is to make the model actually fail. Phase 4's analyzer
    /// takes the opposite path on purpose — there the model IS the feature — so this
    /// flag is what lets both be asserted rather than assumed.
    /// </para>
    /// </summary>
    public Exception? Throws { get; init; }

    /// <summary>A fake standing in for `ollama serve` not running.</summary>
    public static FakeChatClient Unreachable() =>
        new("{}") { Throws = new HttpRequestException("Connection refused (localhost:11434)") };

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastPrompt = string.Join("\n", messages.Select(m => m.Text));
        if (Throws is not null) throw Throws;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The analyzer never streams — it needs the whole JSON document before it
        // can do anything with it. Implemented rather than throwing so the fake is
        // a legal IChatClient, but if this ever runs, a caller changed.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
