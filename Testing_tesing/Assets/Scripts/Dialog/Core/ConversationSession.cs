using System;
using System.Collections.Generic;

namespace Dialog
{
    // One conversation between the player and a character: the message history, the
    // turn limits from <ConversationRules>, the Actor LLM replies, and after every reply
    // the turn analysis that moves rapport. No Unity code, so the console harness can
    // run the exact same flow.
    //
    // Per turn: player message -> actor reply (streamed) -> analyzer labels the message
    // -> RapportModel updates -> next actor prompt carries the new mood.
    public class ConversationSession
    {
        public const string EndTurnLimit = "turn_limit";
        public const string EndBoundaryStrikes = "boundary_strikes";

        public CharacterDefinition Character { get; }
        public RapportModel Rapport { get; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public int PlayerTurns { get; private set; }
        public bool IsActorBusy { get; private set; }
        public bool IsAnalyzing { get; private set; }
        public bool IsOver { get; private set; }
        public string EndReason { get; private set; }

        public bool CanSend => !IsOver && !IsActorBusy && !IsAnalyzing;
        public int TurnsLeft => Character.Rules.MaxTurns - PlayerTurns;
        public bool ReachedMinTurns => PlayerTurns >= Character.Rules.MinTurns;

        // What was actually said, without the system prompt or hidden notes.
        public IReadOnlyList<ChatMessage> Transcript => transcript;
        public IReadOnlyList<TurnAnalysis> Turns => turns;

        public event Action NpcReplyStarted;
        public event Action<string> NpcToken;
        public event Action<string> NpcReplyCompleted;
        public event Action<string> ReplyFailed;
        public event Action<TurnAnalysis> TurnAnalyzed;
        public event Action<string> Ended;

        readonly ILlmClient client;
        readonly ModelRole actor;
        readonly AnalysisDefinition analysis;
        readonly TurnAnalyzer analyzer;
        readonly ChatMessage systemPrompt;
        // Hidden notes are never stored here; they are added to each actor request only.
        readonly List<ChatMessage> transcript = new List<ChatMessage>();
        readonly List<TurnAnalysis> turns = new List<TurnAnalysis>();
        ILlmCall actorCall;
        ILlmCall analyzerCall;

        public ConversationSession(CharacterDefinition character, GameData data, ILlmClient client)
        {
            Character = character;
            this.client = client;
            actor = data.Settings.Actor;
            analysis = data.Analysis;
            Rapport = new RapportModel(character, analysis);
            analyzer = new TurnAnalyzer(client, data.Settings.Analyzer, character, analysis);

            systemPrompt = new ChatMessage("system", PromptBuilder.BuildActorPrompt(character));
            transcript.Add(new ChatMessage("assistant", character.Greeting));
        }

        // Returns false if the message can't be sent right now (busy, analysing or over).
        public bool SendPlayerMessage(string text)
        {
            if (!CanSend || string.IsNullOrWhiteSpace(text))
                return false;

            PlayerTurns++;
            bool lastTurn = TurnsLeft <= 0;
            transcript.Add(new ChatMessage("user", text.Trim()));
            int playerIndex = transcript.Count - 1;

            string note = Rapport.MoodNote();
            if (lastTurn)
                note += " This is the last exchange: reply, then bring the conversation to a natural close.";

            RequestActorReply(BuildActorMessages(note, null),
                onDone: () =>
                {
                    if (lastTurn)
                        End(EndTurnLimit);
                    AnalyzeTurn(playerIndex);
                },
                onFailed: () =>
                {
                    // Undo the turn so the player can simply try again.
                    PlayerTurns--;
                    transcript.RemoveAt(playerIndex);
                });
            return true;
        }

        // Stops everything; called when the player closes the conversation.
        public void Close(string reason)
        {
            actorCall?.Abort();
            analyzerCall?.Abort();
            actorCall = analyzerCall = null;
            IsActorBusy = IsAnalyzing = false;
            End(reason);
        }

        void AnalyzeTurn(int playerIndex)
        {
            IsAnalyzing = true;
            int turnNumber = PlayerTurns;
            analyzerCall = analyzer.Analyze(transcript, playerIndex, turnNumber, result =>
            {
                analyzerCall = null;
                IsAnalyzing = false;

                if (result.Failed)
                    DialogLog.Warning($"[Analyzer] turn {turnNumber}: {result.Error}");
                else
                    Rapport.Apply(result);
                turns.Add(result);
                TurnAnalyzed?.Invoke(result);

                if (!IsOver && Rapport.StruckOut)
                    SayGoodbye();
            });
        }

        // Too many boundary strikes: the character ends the conversation in their own words.
        void SayGoodbye()
        {
            // There is no new player line, so a placeholder user turn carries the instruction.
            var cue = new ChatMessage("user", "(The other person is waiting for your reply.)");
            RequestActorReply(BuildActorMessages(analysis.Moods.Leave, cue),
                onDone: () => End(EndBoundaryStrikes),
                onFailed: () => End(EndBoundaryStrikes));
        }

        void RequestActorReply(List<ChatMessage> messages, Action onDone, Action onFailed)
        {
            IsActorBusy = true;
            NpcReplyStarted?.Invoke();
            actorCall = client.Chat(new LlmRequest { Role = actor, Messages = messages },
                token => NpcToken?.Invoke(token),
                reply =>
                {
                    actorCall = null;
                    IsActorBusy = false;
                    reply = CleanReply(reply);
                    transcript.Add(new ChatMessage("assistant", reply));
                    NpcReplyCompleted?.Invoke(reply);
                    onDone();
                },
                error =>
                {
                    actorCall = null;
                    IsActorBusy = false;
                    onFailed();
                    ReplyFailed?.Invoke(error);
                });
        }

        // System prompt + transcript, with the hidden note appended to the last user line
        // (Gemma has no mid-conversation system role). extraUser is a line that isn't in the transcript.
        List<ChatMessage> BuildActorMessages(string note, ChatMessage extraUser)
        {
            var messages = new List<ChatMessage> { systemPrompt };
            messages.AddRange(transcript);
            if (extraUser != null)
                messages.Add(extraUser);

            int last = messages.Count - 1;
            messages[last] = new ChatMessage(messages[last].role,
                messages[last].content + "\n\n(Private note, never mention it: " + note + ")");
            return messages;
        }

        void End(string reason)
        {
            if (IsOver)
                return;
            IsOver = true;
            EndReason = reason;
            Ended?.Invoke(reason);
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
