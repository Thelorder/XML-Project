using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dialog.Harness
{
    // ILlmClient over HttpClient. Callbacks run on a worker thread, which is fine here:
    // the harness waits for each call before starting the next.
    public class HttpOllamaClient : ILlmClient
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        readonly string baseUrl;
        readonly string keepAlive;

        public HttpOllamaClient(string baseUrl, string keepAlive)
        {
            this.baseUrl = baseUrl;
            this.keepAlive = keepAlive;
        }

        public void Warmup(ModelRole role, Action<bool> onDone)
        {
            string json = OllamaProtocol.BuildChatRequest(new LlmRequest { Role = role }, keepAlive, false);
            Task.Run(async () =>
            {
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await Http.PostAsync(baseUrl + OllamaProtocol.ChatPath, content);
                    onDone?.Invoke(response.IsSuccessStatusCode);
                }
                catch (Exception)
                {
                    onDone?.Invoke(false);
                }
            });
        }

        public ILlmCall Chat(LlmRequest request, Action<string> onToken, Action<string> onComplete, Action<string> onError)
        {
            var call = new Call();
            string json = OllamaProtocol.BuildChatRequest(request, keepAlive, true);
            Task.Run(async () =>
            {
                var reply = new StringBuilder();
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + OllamaProtocol.ChatPath)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                    using HttpResponseMessage response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, call.Token);
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(call.Token));

                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (!OllamaProtocol.TryParseChunk(line, out string content, out _, out string error))
                            continue;
                        if (!string.IsNullOrEmpty(error))
                        {
                            onError?.Invoke(error);
                            return;
                        }
                        if (!string.IsNullOrEmpty(content))
                        {
                            reply.Append(content);
                            onToken?.Invoke(content);
                        }
                    }
                    if (!response.IsSuccessStatusCode)
                        onError?.Invoke($"HTTP {(int)response.StatusCode}");
                    else
                        onComplete?.Invoke(reply.ToString());
                }
                catch (OperationCanceledException)
                {
                    // aborted: no callback, like the Unity client
                }
                catch (Exception e)
                {
                    onError?.Invoke(e.Message);
                }
            });
            return call;
        }

        class Call : ILlmCall
        {
            readonly CancellationTokenSource cancel = new CancellationTokenSource();
            public CancellationToken Token => cancel.Token;
            public void Abort() => cancel.Cancel();
        }
    }
}
