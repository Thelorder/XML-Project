using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Dialog
{
    // ILlmClient over UnityWebRequest. Streams the reply token by token so text appears
    // as soon as the model starts generating. All callbacks run on the main thread.
    public class OllamaClient : ILlmClient
    {
        readonly string baseUrl;
        readonly string keepAlive;

        public OllamaClient(string baseUrl, string keepAlive)
        {
            this.baseUrl = baseUrl;
            this.keepAlive = keepAlive;
        }

        // Loads the model into VRAM ahead of time; the first load can take 30+ seconds.
        public void Warmup(ModelRole role, Action<bool> onDone)
        {
            string json = OllamaProtocol.BuildChatRequest(new LlmRequest { Role = role }, keepAlive, false);
            UnityWebRequest web = CreatePost(json, new DownloadHandlerBuffer());
            web.SendWebRequest().completed += _ =>
            {
                bool ok = web.result == UnityWebRequest.Result.Success;
                if (!ok)
                    Debug.LogWarning($"[Ollama] Warm-up of '{role.Model}' failed: {web.error} {web.downloadHandler.text}. Is Ollama running at {baseUrl}?");
                web.Dispose();
                onDone?.Invoke(ok);
            };
        }

        public ILlmCall Chat(LlmRequest request, Action<string> onToken, Action<string> onComplete, Action<string> onError)
        {
            var reply = new StringBuilder();
            string streamError = null;
            var handler = new LineStreamHandler(line =>
            {
                if (!OllamaProtocol.TryParseChunk(line, out string content, out _, out string error))
                    return;
                if (!string.IsNullOrEmpty(error))
                    streamError = error;
                else if (!string.IsNullOrEmpty(content))
                {
                    reply.Append(content);
                    onToken?.Invoke(content);
                }
            });

            var call = new Call(CreatePost(OllamaProtocol.BuildChatRequest(request, keepAlive, true), handler));
            call.Web.SendWebRequest().completed += _ =>
            {
                UnityWebRequest web = call.Web;
                if (!call.Aborted)
                {
                    if (web.result == UnityWebRequest.Result.Success && streamError == null)
                        onComplete?.Invoke(reply.ToString());
                    else
                        onError?.Invoke(streamError ?? $"{web.error} ({baseUrl})");
                }
                web.Dispose();
            };
            return call;
        }

        UnityWebRequest CreatePost(string json, DownloadHandler download)
        {
            var web = new UnityWebRequest(baseUrl + OllamaProtocol.ChatPath, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = download,
                timeout = 180,
            };
            web.SetRequestHeader("Content-Type", "application/json");
            return web;
        }

        class Call : ILlmCall
        {
            public readonly UnityWebRequest Web;
            public bool Aborted { get; private set; }

            public Call(UnityWebRequest web)
            {
                Web = web;
            }

            public void Abort()
            {
                if (Aborted)
                    return;
                Aborted = true;
                Web.Abort();
            }
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
