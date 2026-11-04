using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Dialog
{
    public class ChatMessage
    {
        public string role; // system | user | assistant
        public string content;

        public ChatMessage(string role, string content)
        {
            this.role = role;
            this.content = content;
        }
    }

    public class LlmRequest
    {
        public ModelRole Role;
        public IReadOnlyList<ChatMessage> Messages;
        // JSON schema the reply must follow (Ollama "format"); null = free text.
        public JObject Schema;
    }

    public interface ILlmCall
    {
        void Abort();
    }

    // Transport to a chat model. The Unity game uses UnityWebRequest, the console
    // harness uses HttpClient; everything above this interface is shared.
    // Callbacks must arrive on the caller's thread in Unity (main thread).
    public interface ILlmClient
    {
        ILlmCall Chat(LlmRequest request, Action<string> onToken, Action<string> onComplete, Action<string> onError);
        void Warmup(ModelRole role, Action<bool> onDone);
    }

    // Shared (de)serialisation of Ollama's /api/chat so both transports send identical requests.
    public static class OllamaProtocol
    {
        public const string ChatPath = "/api/chat";

        public static string BuildChatRequest(LlmRequest request, string keepAlive, bool stream)
        {
            var messages = new JArray();
            if (request.Messages != null)
            {
                foreach (ChatMessage m in request.Messages)
                    messages.Add(new JObject { ["role"] = m.role, ["content"] = m.content });
            }

            var body = new JObject
            {
                ["model"] = request.Role.Model,
                ["messages"] = messages,
                ["stream"] = stream,
                ["think"] = false,
                ["keep_alive"] = keepAlive,
                // num_ctx must be identical for every call, otherwise Ollama reloads the model.
                ["options"] = new JObject
                {
                    ["temperature"] = request.Role.Temperature,
                    ["num_ctx"] = request.Role.ContextLength,
                    ["num_predict"] = request.Role.MaxTokens,
                },
            };
            if (request.Schema != null)
                body["format"] = request.Schema;
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        // One line of the streamed reply. Returns false if the line is not valid JSON.
        public static bool TryParseChunk(string line, out string content, out bool done, out string error)
        {
            content = null;
            done = false;
            error = null;
            try
            {
                JObject chunk = JObject.Parse(line);
                error = (string)chunk["error"];
                content = (string)chunk["message"]?["content"];
                done = (bool?)chunk["done"] ?? false;
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }
    }

    // Core code has no UnityEngine, so logging goes through here; DialogManager and the
    // harness plug in Debug.Log / Console.
    public static class DialogLog
    {
        public static Action<string> Info = _ => { };
        public static Action<string> Warning = _ => { };
    }
}
