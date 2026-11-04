using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Dialog.Harness
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "analyze";
            string dataRoot = FindDataRoot();
            DialogLog.Info = m => Console.WriteLine("[info] " + m);
            DialogLog.Warning = m => Console.WriteLine("[warn] " + m);

            var errors = new List<string>();
            GameData data = XmlDataLoader.LoadAll(dataRoot, errors);
            foreach (string e in errors)
                Console.WriteLine("[xml error] " + e);
            if (data.Settings == null || data.Analysis == null)
                return 1;

            var client = new HttpOllamaClient(data.Settings.OllamaUrl, data.Settings.KeepAlive);
            switch (mode)
            {
                case "analyze":
                    return RunCases(data, client, args.Length > 1 ? args[1] : "analyzer-cases.xml");
                case "chat":
                    return Chat(data, client, args.Length > 1 ? args[1] : "maya");
                case "prompt":
                    CharacterDefinition c = data.FindCharacter(args.Length > 1 ? args[1] : "maya");
                    Console.WriteLine(PromptBuilder.BuildActorPrompt(c));
                    Console.WriteLine("==========");
                    Console.WriteLine(new TurnAnalyzer(client, data.Settings.Analyzer, c, data.Analysis).SystemPrompt);
                    return 0;
                default:
                    Console.WriteLine("usage: dotnet run -- [analyze [cases.xml] | chat <characterId> | prompt <characterId>]");
                    return 1;
            }
        }

        // ---------- analyze: accuracy of the turn analyzer ----------

        static int RunCases(GameData data, ILlmClient client, string casesFile)
        {
            XElement root = XDocument.Load(casesFile).Root;
            CharacterDefinition character = data.FindCharacter((string)root.Attribute("character"));
            var analyzer = new TurnAnalyzer(client, data.Settings.Analyzer, character, data.Analysis);

            var expectedHits = new Dictionary<string, int>();
            var expectedTotal = new Dictionary<string, int>();
            var falsePositives = new Dictionary<string, int>();
            int passed = 0, total = 0, unverifiedEvidence = 0, labels = 0;
            var timer = new Stopwatch();

            foreach (XElement c in root.Elements("Case"))
            {
                total++;
                var transcript = new List<ChatMessage>();
                foreach (XElement line in c.Elements().Where(e => e.Name != "Judge"))
                    transcript.Add(new ChatMessage(line.Name == "Npc" ? "assistant" : "user", line.Value.Trim()));
                if (transcript.Count == 0)
                    transcript.Add(new ChatMessage("assistant", character.Greeting));
                transcript.Add(new ChatMessage("user", c.Element("Judge").Value.Trim()));

                timer.Restart();
                TurnAnalysis result = Wait<TurnAnalysis>(done => analyzer.Analyze(transcript, transcript.Count - 1, 1, done));
                long ms = timer.ElapsedMilliseconds;

                var failures = new List<string>();
                if (result.Failed)
                    failures.Add("analyzer failed: " + result.Error);

                var found = new HashSet<string>(result.Behaviours.Select(b => b.Tag));
                labels += result.Behaviours.Count;
                unverifiedEvidence += result.Behaviours.Count(b => !b.EvidenceFound);

                foreach (string tag in Split(c, "expect"))
                {
                    Count(expectedTotal, tag);
                    if (found.Contains(tag))
                        Count(expectedHits, tag);
                    else
                        failures.Add("missing " + tag);
                }
                foreach (string tag in Split(c, "forbid"))
                {
                    if (!found.Contains(tag))
                        continue;
                    Count(falsePositives, tag);
                    failures.Add("unwanted " + tag);
                }

                string boundary = (string)c.Attribute("boundary");
                if (boundary != null && (result.BoundaryCrossed ?? "none") != boundary)
                    failures.Add($"boundary {result.BoundaryCrossed ?? "none"} (expected {boundary})");

                string cue = (string)c.Attribute("cue");
                if (cue != null && result.Cue != cue)
                    failures.Add($"cue {result.Cue} (expected {cue})");

                string trigger = (string)c.Attribute("trigger");
                if (trigger == "none")
                {
                    if (result.Triggers.Count > 0)
                        failures.Add("unwanted trigger " + string.Join(",", result.Triggers.Select(t => t.Id)));
                }
                else if (trigger != null)
                {
                    string[] parts = trigger.Split(':');
                    TriggerHit hit = result.Triggers.FirstOrDefault(t => t.Id == parts[0]);
                    if (hit == null)
                        failures.Add($"trigger {parts[0]} not reported");
                    else if ((hit.HandledWell ? "well" : "badly") != parts[1])
                        failures.Add($"trigger {parts[0]} handled {(hit.HandledWell ? "well" : "badly")} (expected {parts[1]})");
                }

                if (failures.Count == 0)
                    passed++;
                string labelsText = string.Join(", ", result.Behaviours.Select(b => b.Tag + (b.EvidenceFound ? "" : "?")));
                string extra = $"boundary={result.BoundaryCrossed ?? "none"} cue={result.Cue}" +
                               (result.Triggers.Count > 0 ? " triggers=" + string.Join(",", result.Triggers.Select(t => t.Id + ":" + (t.HandledWell ? "well" : "badly"))) : "");
                Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAIL")}  {(string)c.Attribute("id"),-28} {ms,5} ms  [{labelsText}] {extra}");
                foreach (string f in failures)
                    Console.WriteLine("        - " + f);
            }

            Console.WriteLine();
            Console.WriteLine($"Cases passed: {passed}/{total} ({100.0 * passed / total:0}%)");
            Console.WriteLine($"Evidence not found verbatim in the message: {unverifiedEvidence}/{labels} labels (marked ?)");
            Console.WriteLine();
            Console.WriteLine("Per behaviour: found when expected / reported when forbidden");
            foreach (string tag in data.Analysis.Behaviours.Select(b => b.Id))
            {
                expectedTotal.TryGetValue(tag, out int exp);
                expectedHits.TryGetValue(tag, out int hit);
                falsePositives.TryGetValue(tag, out int fp);
                if (exp > 0 || fp > 0)
                    Console.WriteLine($"  {tag,-22} {hit}/{exp}   false positives: {fp}");
            }
            return passed == total ? 0 : 2;
        }

        // ---------- chat: play a conversation in the terminal ----------

        static int Chat(GameData data, ILlmClient client, string characterId)
        {
            CharacterDefinition character = data.FindCharacter(characterId);
            if (character == null)
            {
                Console.WriteLine($"No character '{characterId}'.");
                return 1;
            }

            var session = new ConversationSession(character, data, client);
            session.NpcReplyStarted += () => Console.Write($"{character.Name}: ");
            session.NpcToken += token => Console.Write(token);
            session.NpcReplyCompleted += _ => Console.WriteLine();
            session.ReplyFailed += error => Console.WriteLine("\n[actor error] " + error);
            bool showJson = Environment.GetCommandLineArgs().Contains("--json");
            if (showJson)
                session.TurnAnalyzed += turn => Console.WriteLine("   " + turn.RawJson);
            session.TurnAnalyzed += turn => Console.WriteLine(turn.Failed
                ? "   [analysis failed] " + turn.Error
                : $"   [{string.Join(", ", turn.Breakdown.DefaultIfEmpty("nothing"))}] -> rapport {turn.RapportAfter:0} ({turn.RapportDelta:+0;-0;0}), {session.Rapport.Band}, strikes {session.Rapport.Strikes}/{session.Rapport.MaxStrikes}");
            session.Ended += reason => Console.WriteLine($"   [conversation ended: {reason}]");

            Console.WriteLine($"{character.Name}: {character.Greeting}");
            Console.WriteLine($"   [rapport {session.Rapport.Value:0}, {session.Rapport.Band}]  (empty line = quit)");
            while (!session.IsOver)
            {
                Console.Write("You: ");
                string line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    break;
                if (Console.IsInputRedirected)
                    Console.WriteLine(line);
                if (!session.SendPlayerMessage(line))
                    continue;
                // Wait for reply, analysis and a possible goodbye line.
                Thread.Sleep(100);
                while (session.IsActorBusy || session.IsAnalyzing)
                    Thread.Sleep(50);
            }
            session.Close("player_ended");
            return 0;
        }

        // ---------- helpers ----------

        static T Wait<T>(Action<Action<T>> start)
        {
            var done = new TaskCompletionSource<T>();
            start(result => done.TrySetResult(result));
            return done.Task.GetAwaiter().GetResult();
        }

        static IEnumerable<string> Split(XElement e, string attribute)
        {
            return ((string)e.Attribute(attribute) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        static void Count(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        // Walks up from the build output to find the Unity project's data folder.
        static string FindDataRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "Assets", "StreamingAssets", "Dialog");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not find Assets/StreamingAssets/Dialog above " + AppContext.BaseDirectory);
        }
    }
}
