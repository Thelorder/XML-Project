using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Dialog
{
    [Serializable]
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

    // Minimal client for Ollama's /api/chat. Streams the reply token by token so text
    // appears as soon as the model starts generating. All callbacks run on the main thread.
    public class OllamaClient
    {
        [Serializable]
        class ChatOptions
        {
            public float temperature;
            public int num_ctx;
            public int num_predict;
        }

        [Serializable]
        class ChatRequest
        {
            public string model;
            public ChatMessage[] messages;
            public bool stream;
            public bool think;
            public string keep_alive;
            public ChatOptions options;
        }

        [Serializable]
        class ChatChunk
        {
            public ChatMessage message;
            public bool done;
            public string error;
        }

        readonly string baseUrl;
        readonly string keepAlive;

        public OllamaClient(string baseUrl, string keepAlive)
        {
            this.baseUrl = baseUrl;
            this.keepAlive = keepAlive;
        }

        // Loads the model into VRAM ahead of time; the first load can take 30+ seconds.
        // Uses the role's own options: a different num_ctx would make Ollama reload the model later.
        public void Warmup(ModelRole role, Action<bool> onDone = null)
        {
            ChatRequest request = CreateRequest(role, new ChatMessage[0], false);
            UnityWebRequest web = CreatePost("/api/chat", JsonUtility.ToJson(request), new DownloadHandlerBuffer());
            web.SendWebRequest().completed += _ =>
            {
                bool ok = web.result == UnityWebRequest.Result.Success;
                if (!ok)
                    Debug.LogWarning($"[Ollama] Warm-up of '{role.Model}' failed: {web.error} {web.downloadHandler.text}. Is Ollama running at {baseUrl}?");
                web.Dispose();
                onDone?.Invoke(ok);
            };
        }

        // Returns the request so the caller can Abort() it (e.g. the player walks away).
        public UnityWebRequest StreamChat(ModelRole role, IList<ChatMessage> messages,
            Action<string> onToken, Action<string> onComplete, Action<string> onError)
        {
            ChatRequest request = CreateRequest(role, new List<ChatMessage>(messages).ToArray(), true);

            var reply = new StringBuilder();
            string streamError = null;
            var handler = new LineStreamHandler(line =>
            {
                ChatChunk chunk;
                try { chunk = JsonUtility.FromJson<ChatChunk>(line); }
                catch (ArgumentException) { return; }

                if (!string.IsNullOrEmpty(chunk.error))
                    streamError = chunk.error;
                else if (chunk.message != null && !string.IsNullOrEmpty(chunk.message.content))
                {
                    reply.Append(chunk.message.content);
                    onToken?.Invoke(chunk.message.content);
                }
            });

            UnityWebRequest web = CreatePost("/api/chat", JsonUtility.ToJson(request), handler);
            web.SendWebRequest().completed += _ =>
            {
                if (web.result == UnityWebRequest.Result.Success && streamError == null)
                    onComplete?.Invoke(reply.ToString());
                else if (web.error != "Request aborted")
                    onError?.Invoke(streamError ?? $"{web.error} ({baseUrl})");
                web.Dispose();
            };
            return web;
        }

        ChatRequest CreateRequest(ModelRole role, ChatMessage[] messages, bool stream)
        {
            return new ChatRequest
            {
                model = role.Model,
                messages = messages,
                stream = stream,
                think = false,
                keep_alive = keepAlive,
                options = new ChatOptions
                {
                    temperature = role.Temperature,
                    num_ctx = role.ContextLength,
                    num_predict = role.MaxTokens,
                },
            };
        }

        UnityWebRequest CreatePost(string path, string json, DownloadHandler download)
        {
            var web = new UnityWebRequest(baseUrl + path, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = download,
                timeout = 180,
            };
            web.SetRequestHeader("Content-Type", "application/json");
            return web;
        }

        // Ollama streams newline-delimited JSON. Buffers raw bytes so a UTF-8 character
        // split across two network chunks is decoded correctly.
        class LineStreamHandler : DownloadHandlerScript
        {
            readonly Action<string> onLine;
            readonly MemoryStream pending = new MemoryStream();

            public LineStreamHandler(Action<string> onLine) : base(new byte[4096])
            {
                this.onLine = onLine;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                for (int i = 0; i < dataLength; i++)
                {
                    if (data[i] == (byte)'\n')
                        FlushLine();
                    else
                        pending.WriteByte(data[i]);
                }
                return true;
            }

            protected override void CompleteContent()
            {
                FlushLine();
            }

            void FlushLine()
            {
                if (pending.Length == 0)
                    return;
                string line = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
                pending.SetLength(0);
                onLine(line);
            }
        }
    }
}
