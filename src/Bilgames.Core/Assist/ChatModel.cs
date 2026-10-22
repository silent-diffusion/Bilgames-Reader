using System.Text.Json.Nodes;

namespace Bilgames.Core.Assist;

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string text) => new("system", text);
    public static ChatMessage User(string text) => new("user", text);
}

/// <summary>
/// A language model that answers in JSON matching a schema (constrained decoding, so the answer
/// always parses). Implemented by a local llama.cpp server on Windows; any engine could stand in.
/// </summary>
public interface IChatModel
{
    /// <summary>Returns the model's JSON answer. <paramref name="maxTokens"/> caps the length of the answer.</summary>
    Task<JsonNode> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, int maxTokens, CancellationToken ct);
}

public class AssistantUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The model ran out of its answer budget before finishing the JSON.</summary>
public sealed class AssistantAnswerTooLongException(string message) : AssistantUnavailableException(message);
