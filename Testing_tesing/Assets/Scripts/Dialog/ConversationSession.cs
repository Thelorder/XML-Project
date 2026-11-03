using System;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace Dialog
{
    // One conversation between the player and a character: the message history,
    // the turn limits from <ConversationRules>, and the calls to the Actor LLM.
    // No Unity UI here, so the evaluators can later reuse the same transcript.
    public class ConversationSession
    {
        public CharacterDefinition Character { get; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public int PlayerTurns { get; private set; }
        public bool IsWaitingForReply { get; private set; }
        public bool IsOver { get; private set; }
        public string EndReason { get; private set; }

        // What was actually said, without the system prompt or hidden notes.
        public IReadOnlyList<ChatMessage> Transcript => transcript;

        public int TurnsLeft => Character.Rules.MaxTurns - PlayerTurns;
        public bool ReachedMinTurns => PlayerTurns >= Character.Rules.MinTurns;

        readonly OllamaClient client;
        readonly ModelRole actor;
        readonly List<ChatMessage> llmMessages = new List<ChatMessage>();
        readonly List<ChatMessage> transcript = new List<ChatMessage>();
        UnityWebRequest pending;

        public ConversationSession(CharacterDefinition character, OllamaClient client, ModelRole actor)
        {
            Character = character;
            this.client = client;
            this.actor = actor;

            llmMessages.Add(new ChatMessage("system", PromptBuilder.BuildActorPrompt(character)));
            AddMessage("assistant", character.Greeting);
        }

        public void SendPlayerMessage(string text, Action<string> onToken, Action<string> onReply, Action<string> onError)
        {
            if (IsOver || IsWaitingForReply || string.IsNullOrWhiteSpace(text))
                return;

            text = text.Trim();
            PlayerTurns++;
            bool lastTurn = TurnsLeft <= 0;

            transcript.Add(new ChatMessage("user", text));
            // The hint is for the model only; the transcript keeps the player's words untouched.
            string forModel = lastTurn
                ? text + "\n\n(Note: this is the last exchange. Reply, then bring the conversation to a natural close.)"
                : text;
            llmMessages.Add(new ChatMessage("user", forModel));

            IsWaitingForReply = true;
            pending = client.StreamChat(actor, llmMessages, onToken,
                reply =>
                {
                    pending = null;
                    IsWaitingForReply = false;
                    reply = CleanReply(reply);
                    AddMessage("assistant", reply);
                    if (lastTurn)
                        End("turn_limit");
                    onReply?.Invoke(reply);
                },
                error =>
                {
                    pending = null;
                    IsWaitingForReply = false;
                    // Undo the turn so the player can simply try again.
                    PlayerTurns--;
                    transcript.RemoveAt(transcript.Count - 1);
                    llmMessages.RemoveAt(llmMessages.Count - 1);
                    onError?.Invoke(error);
                });
        }

        // reason: "turn_limit", "player_left", later also "boundary_strikes".
        public void End(string reason)
        {
            if (IsOver)
                return;
            pending?.Abort();
            pending = null;
            IsWaitingForReply = false;
            IsOver = true;
            EndReason = reason;
        }

        void AddMessage(string role, string content)
        {
            var message = new ChatMessage(role, content);
            llmMessages.Add(message);
            transcript.Add(message);
        }

        // Small models sometimes prefix their name or wrap the line in quotes.
        string CleanReply(string reply)
        {
            reply = reply.Trim();
            string prefix = Character.Name + ":";
            if (reply.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                reply = reply.Substring(prefix.Length).TrimStart();
            if (reply.Length > 1 && reply[0] == '"' && reply[reply.Length - 1] == '"')
                reply = reply.Substring(1, reply.Length - 2);
            return reply;
        }
    }
}
