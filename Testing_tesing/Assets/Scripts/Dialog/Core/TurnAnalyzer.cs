using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dialog
{
    // What the analyzer found in one player message, plus what RapportModel made of it.
    public class TurnAnalysis
    {
        public int Turn;
        public string PlayerMessage;
        public List<BehaviourHit> Behaviours = new List<BehaviourHit>();
        public List<TriggerHit> Triggers = new List<TriggerHit>();
        public string BoundaryCrossed; // boundary id, or null
        public string Cue = "none";    // noticed | missed | none

        // Filled by RapportModel.
        public int RapportDelta;
        public float RapportAfter;
        public bool Strike;
        public List<string> Breakdown = new List<string>();

        // The analyzer's reply exactly as received, for debugging.
        public string RawJson;

        public bool Failed;
        public string Error;
    }

    public class BehaviourHit
    {
        public string Tag;
        public string Evidence;
        // Whether the evidence quote really occurs in the player's message.
        public bool EvidenceFound;
    }

    public class TriggerHit
    {
        public string Id;
        public bool HandledWell;
    }

    // The second model: labels each player message with behaviours from analysis.xml.
    // It only classifies; RapportModel turns the labels into points.
    public class TurnAnalyzer
    {
        const int ContextMessages = 6;

        public string SystemPrompt { get; }
        public JObject Schema { get; }

        readonly ILlmClient client;
        readonly ModelRole role;
        readonly CharacterDefinition character;
        readonly AnalysisDefinition analysis;

        public TurnAnalyzer(ILlmClient client, ModelRole role, CharacterDefinition character, AnalysisDefinition analysis)
        {
            this.client = client;
            this.role = role;
            this.character = character;
            this.analysis = analysis;
            SystemPrompt = BuildSystemPrompt();
            Schema = BuildSchema();
        }

        // Labels transcript[playerIndex] (a "user" message), using the lines before it as context.
        public ILlmCall Analyze(IReadOnlyList<ChatMessage> transcript, int playerIndex, int turn, Action<TurnAnalysis> onDone)
        {
            string playerMessage = transcript[playerIndex].content;
            var request = new LlmRequest
            {
                Role = role,
                Schema = Schema,
                Messages = new[]
                {
                    new ChatMessage("system", SystemPrompt),
                    new ChatMessage("user", BuildUserPrompt(transcript, playerIndex)),
                },
            };

            return client.Chat(request, null,
                reply => onDone(Parse(reply, playerMessage, turn)),
                error => onDone(new TurnAnalysis { Turn = turn, PlayerMessage = playerMessage, Failed = true, Error = error }));
        }

        TurnAnalysis Parse(string reply, string playerMessage, int turn)
        {
            var result = new TurnAnalysis { Turn = turn, PlayerMessage = playerMessage, RawJson = reply };
            try
            {
                JObject json = JObject.Parse(reply);

                foreach (JToken b in json["behaviours"] ?? new JArray())
                {
                    string tag = (string)b["tag"];
                    if (analysis.FindBehaviour(tag) == null || result.Behaviours.Any(h => h.Tag == tag))
                        continue;
                    string evidence = (string)b["evidence"] ?? "";
                    result.Behaviours.Add(new BehaviourHit
                    {
                        Tag = tag,
                        Evidence = evidence,
                        EvidenceFound = Contains(playerMessage, evidence),
                    });
                }

                // One field per trigger, so "not mentioned" is an explicit answer.
                JObject triggers = json["triggers"] as JObject;
                foreach (Trigger trigger in character.Triggers)
                {
                    string handled = (string)triggers?[trigger.Id];
                    if (handled == "well" || handled == "badly")
                        result.Triggers.Add(new TriggerHit { Id = trigger.Id, HandledWell = handled == "well" });
                }

                string boundary = (string)json["boundary"];
                if (character.Boundaries.Any(b => b.Id == boundary))
                    result.BoundaryCrossed = boundary;

                // Two simple yes/no questions instead of one three-way judgement.
                bool hinted = (bool?)json["npc_hinted_feeling"] ?? false;
                bool acknowledged = (bool?)json["player_acknowledged_feeling"] ?? false;
                result.Cue = !hinted ? "none" : acknowledged ? "noticed" : "missed";

                ApplyWordCountRules(result);
            }
            catch (JsonException e)
            {
                result.Failed = true;
                result.Error = "Analyzer returned invalid JSON: " + e.Message;
            }
            return result;
        }

        // Behaviours with autoMaxWords are decided by counting words, which the model is
        // unreliable at. A reply that short also can't really engage with a topic, so
        // "well" trigger hits are dropped ("lol nice" is not interest in her cat).
        void ApplyWordCountRules(TurnAnalysis result)
        {
            string message = result.PlayerMessage.Trim();
            int words = message.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (message.EndsWith("?"))
                return;

            bool tagged = false;
            foreach (BehaviourDefinition b in analysis.Behaviours)
            {
                if (b.AutoMaxWords <= 0 || words > b.AutoMaxWords)
                    continue;
                tagged = true;
                if (result.Behaviours.All(h => h.Tag != b.Id))
                    result.Behaviours.Add(new BehaviourHit { Tag = b.Id, Evidence = message, EvidenceFound = true });
            }
            if (tagged)
                result.Triggers.RemoveAll(t => t.HandledWell);
        }

        static bool Contains(string message, string evidence)
        {
            string Normalize(string s) => string.Join(" ", s.ToLowerInvariant().Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            string e = Normalize(evidence).Trim('"', '\'', '.', ' ');
            return e.Length > 0 && Normalize(message).Contains(e);
        }

        string BuildSystemPrompt()
        {
            string name = character.Name;
            var sb = new StringBuilder();
            sb.AppendLine($"You are a communication-skills analyst. You do not take part in the conversation. You read a conversation between a PLAYER and {name}, and you label ONLY the player's latest message.");
            sb.AppendLine();
            sb.AppendLine($"## About {name}");
            sb.AppendLine($"{name}, {character.Age}, {character.Occupation}. {character.Background}");
            sb.AppendLine();

            sb.AppendLine($"## {name}'s personal boundaries (ids)");
            foreach (Boundary b in character.Boundaries)
                sb.AppendLine($"- {b.Id}: {b.Description}");
            sb.AppendLine();

            if (character.Triggers.Count > 0)
            {
                sb.AppendLine($"## Topics {name} reacts strongly to (ids)");
                foreach (Trigger t in character.Triggers)
                    sb.AppendLine($"- {t.Id}: {t.Topics}");
                sb.AppendLine();
            }

            if (character.EmotionalCues.Count > 0)
            {
                sb.AppendLine($"## Feelings {name} may hint at");
                foreach (EmotionalCue c in character.EmotionalCues)
                    sb.AppendLine($"- {c.Description}");
                sb.AppendLine();
            }

            sb.AppendLine("## Behaviour labels (use only these ids; a message can have several labels or none)");
            foreach (BehaviourDefinition b in analysis.Behaviours)
            {
                sb.Append($"- {b.Id}: {b.Description}");
                if (b.Examples.Count > 0)
                    sb.Append(" Examples: " + string.Join(" | ", b.Examples.Select(x => "\"" + x + "\"")));
                sb.AppendLine();
            }
            sb.AppendLine();

            sb.AppendLine("## Rules");
            sb.AppendLine($"- Judge only what the latest player message does. Never label {name}'s lines.");
            sb.AppendLine("- evidence: copy the exact words from the latest player message that show the behaviour.");
            sb.AppendLine("- Only use a label when the message clearly shows it. An ordinary friendly question is just open_question (or nothing), not a boundary problem.");
            sb.AppendLine("- A short reply that adds nothing (\"ok\", \"cool\", \"nice\", \"lol\") is low_effort.");
            sb.AppendLine("- If the latest player message does not relate to what " + name + " just said, label it changes_subject.");
            sb.AppendLine("- A question about something " + name + " just mentioned is follow_up (it can also be open_question).");
            sb.AppendLine($"- npc_hinted_feeling: true only if {name}'s line just before the latest player message hints at one of the feelings listed above.");
            sb.AppendLine("- player_acknowledged_feeling: true only if the latest player message responds to that feeling with understanding or care. Advice, jokes, a new topic or brushing it off are false.");
            sb.AppendLine($"- triggers: for EACH topic id answer \"not_mentioned\" unless the latest player message itself talks about that topic. If it does: \"well\" if respectful or interested, \"badly\" if mocking or dismissive. Topics mentioned only by {name} do not count.");
            sb.AppendLine("- boundary: the id of a boundary the latest player message clearly crosses, otherwise \"none\".");
            sb.AppendLine("- Return only the JSON object.");
            return sb.ToString();
        }

        string BuildUserPrompt(IReadOnlyList<ChatMessage> transcript, int playerIndex)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Conversation so far (oldest first):");
            int start = Math.Max(0, playerIndex - ContextMessages);
            for (int i = start; i < playerIndex; i++)
                sb.AppendLine(Speaker(transcript[i]) + ": " + transcript[i].content);
            sb.AppendLine();
            sb.AppendLine("Latest player message to label:");
            sb.AppendLine("PLAYER: " + transcript[playerIndex].content);
            return sb.ToString();
        }

        string Speaker(ChatMessage m)
        {
            return m.role == "user" ? "PLAYER" : character.Name.ToUpperInvariant();
        }

        // JSON schema for Ollama's constrained output; enums come straight from the XML,
        // so the model cannot invent labels, triggers or boundaries.
        JObject BuildSchema()
        {
            JObject StringEnum(IEnumerable<string> values) =>
                new JObject { ["type"] = "string", ["enum"] = new JArray(values.Cast<object>().ToArray()) };

            var behaviourItem = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["tag"] = StringEnum(analysis.Behaviours.Select(b => b.Id)),
                    ["evidence"] = new JObject { ["type"] = "string" },
                },
                ["required"] = new JArray("tag", "evidence"),
            };

            // { "chess": "not_mentioned" | "well" | "badly", "pixel": ... }
            var triggerProperties = new JObject();
            foreach (Trigger t in character.Triggers)
                triggerProperties[t.Id] = StringEnum(new[] { "not_mentioned", "well", "badly" });
            var triggers = new JObject
            {
                ["type"] = "object",
                ["properties"] = triggerProperties,
                ["required"] = new JArray(character.Triggers.Select(t => (object)t.Id).ToArray()),
            };

            // Property order is the order the model writes them in: the simple cue
            // questions first, then the labels.
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["npc_hinted_feeling"] = new JObject { ["type"] = "boolean" },
                    ["player_acknowledged_feeling"] = new JObject { ["type"] = "boolean" },
                    ["behaviours"] = new JObject { ["type"] = "array", ["items"] = behaviourItem },
                    ["triggers"] = triggers,
                    ["boundary"] = StringEnum(new[] { "none" }.Concat(character.Boundaries.Select(b => b.Id))),
                },
                ["required"] = new JArray("npc_hinted_feeling", "player_acknowledged_feeling", "behaviours", "triggers", "boundary"),
            };
        }
    }
}
