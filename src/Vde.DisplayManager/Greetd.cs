using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vde.DisplayManager;

// The conversation between a greeter (the login screen) and the display manager. It is greetd's protocol, so any
// greeter written for greetd (regreet, tuigreet, gtkgreet) can be used with vdedm, and ours works with greetd.
// Each message is a JSON object preceded by its length as a 4-byte unsigned integer in the machine's byte order.
//
//   greeter -> manager   create_session {username}        post_auth_message_response {response}
//                        start_session {cmd, env}         cancel_session
//   manager -> greeter   success   error {error_type, description}   auth_message {auth_message_type, auth_message}
public abstract record GreeterRequest;
public sealed record CreateSession(string Username) : GreeterRequest;
public sealed record PostAuthMessageResponse(string? Response) : GreeterRequest;
public sealed record StartSession(IReadOnlyList<string> Cmd, IReadOnlyList<string> Env) : GreeterRequest;
public sealed record CancelSession : GreeterRequest;

public enum AuthMessageType { Visible, Secret, Info, Error }
public enum ErrorType { Error, AuthError }

public abstract record GreeterResponse;
public sealed record Success : GreeterResponse;
public sealed record ErrorResponse(ErrorType Type, string Description) : GreeterResponse;
public sealed record AuthMessage(AuthMessageType Type, string Text) : GreeterResponse;

public static class Greetd
{
    public const int MaxMessage = 1 << 20;   // a greeter that sends more than a megabyte is not one we talk to

    public static byte[] Encode(GreeterRequest request)
    {
        var node = request switch
        {
            CreateSession c => new JsonObject { ["type"] = "create_session", ["username"] = c.Username },
            PostAuthMessageResponse p => new JsonObject { ["type"] = "post_auth_message_response", ["response"] = p.Response },
            StartSession s => new JsonObject { ["type"] = "start_session", ["cmd"] = new JsonArray(s.Cmd.Select(x => (JsonNode?)x).ToArray()), ["env"] = new JsonArray(s.Env.Select(x => (JsonNode?)x).ToArray()) },
            CancelSession => new JsonObject { ["type"] = "cancel_session" },
            _ => throw new ArgumentException("Unknown request."),
        };
        return Frame(node);
    }

    public static byte[] Encode(GreeterResponse response)
    {
        var node = response switch
        {
            Success => new JsonObject { ["type"] = "success" },
            ErrorResponse e => new JsonObject { ["type"] = "error", ["error_type"] = e.Type == ErrorType.AuthError ? "auth_error" : "error", ["description"] = e.Description },
            AuthMessage m => new JsonObject { ["type"] = "auth_message", ["auth_message_type"] = m.Type.ToString().ToLowerInvariant(), ["auth_message"] = m.Text },
            _ => throw new ArgumentException("Unknown response."),
        };
        return Frame(node);
    }

    private static byte[] Frame(JsonNode node)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(node);
        var framed = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(framed.AsSpan(0, 4), (uint)body.Length);   // native byte order, as the protocol says
        body.CopyTo(framed, 4);
        return framed;
    }

    // Throws InvalidDataException for anything that is not a well-formed request.
    public static GreeterRequest DecodeRequest(ReadOnlySpan<byte> body)
    {
        var root = Parse(body);
        string Str(string name) => root[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new InvalidDataException($"'{name}' is missing or not text");
        IReadOnlyList<string> List(string name)
            => root[name] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new InvalidDataException($"'{name}' must hold text")).ToList()
                : name == "env" ? [] : throw new InvalidDataException($"'{name}' is missing");
        return Str("type") switch
        {
            "create_session" => new CreateSession(Str("username")),
            "post_auth_message_response" => new PostAuthMessageResponse(root["response"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null),
            "start_session" => new StartSession(List("cmd"), List("env")),
            "cancel_session" => new CancelSession(),
            var other => throw new InvalidDataException($"unknown request '{other}'"),
        };
    }

    public static GreeterResponse DecodeResponse(ReadOnlySpan<byte> body)
    {
        var root = Parse(body);
        string Str(string name) => root[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new InvalidDataException($"'{name}' is missing or not text");
        return Str("type") switch
        {
            "success" => new Success(),
            "error" => new ErrorResponse(Str("error_type") == "auth_error" ? ErrorType.AuthError : ErrorType.Error, Str("description")),
            "auth_message" => new AuthMessage(Enum.TryParse<AuthMessageType>(Str("auth_message_type"), true, out var t) ? t : throw new InvalidDataException("unknown message type"), Str("auth_message")),
            var other => throw new InvalidDataException($"unknown response '{other}'"),
        };
    }

    private static JsonObject Parse(ReadOnlySpan<byte> body)
    {
        try { return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("not a JSON object"); }
        catch (JsonException e) { throw new InvalidDataException("not JSON: " + e.Message); }
    }

    // Reads one framed message; null at the end of the stream.
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancel = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancel)) return null;
        var length = BitConverter.ToUInt32(header);
        if (length > MaxMessage) throw new InvalidDataException("message too large");
        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, cancel)) throw new InvalidDataException("message cut short");
        return body;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(got), cancel);
            if (n == 0) return got == 0 ? false : throw new InvalidDataException("stream ended inside a message");
            got += n;
        }
        return true;
    }
}
