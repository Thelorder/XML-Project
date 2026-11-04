using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Dialog
{
    // Everything the game reads from XML.
    public class GameData
    {
        public GameSettings Settings;
        public AnalysisDefinition Analysis;
        public List<CharacterDefinition> Characters = new List<CharacterDefinition>();

        public CharacterDefinition FindCharacter(string id)
        {
            return Characters.FirstOrDefault(c => c.Id == id);
        }
    }

    // Reads the game's XML data (StreamingAssets/Dialog in Unity), validating every file
    // against its XSD first. Invalid files are skipped and reported, never half-loaded.
    // The data folder is passed in, so the console harness can load the same files.
    public static class XmlDataLoader
    {
        public static GameData LoadAll(string dataRoot, List<string> errors)
        {
            var data = new GameData
            {
                Settings = LoadSettings(dataRoot, errors),
                Analysis = LoadAnalysis(dataRoot, errors),
                Characters = LoadCharacters(dataRoot, errors),
            };
            if (data.Analysis != null)
                CheckCrossReferences(data, errors);
            return data;
        }

        public static GameSettings LoadSettings(string dataRoot, List<string> errors)
        {
            string path = Path.Combine(dataRoot, "settings.xml");
            XDocument doc = LoadValidated(path, Schema(dataRoot, "settings.xsd"), errors);
            if (doc == null)
                return null;

            XElement root = doc.Root;
            XElement ollama = root.Element("Ollama");
            return new GameSettings
            {
                OllamaUrl = ((string)ollama.Attribute("url")).TrimEnd('/'),
                KeepAlive = (string)ollama.Attribute("keepAlive") ?? "30m",
                Actor = ReadModelRole(root.Element("Actor")),
                Analyzer = ReadModelRole(root.Element("Analyzer")),
                Evaluator = ReadModelRole(root.Element("Evaluator")),
            };
        }

        public static AnalysisDefinition LoadAnalysis(string dataRoot, List<string> errors)
        {
            string path = Path.Combine(dataRoot, "analysis.xml");
            XDocument doc = LoadValidated(path, Schema(dataRoot, "analysis.xsd"), errors);
            if (doc == null)
                return null;

            XElement root = doc.Root;
            XElement scoring = root.Element("Scoring");
            XElement moods = root.Element("Moods");
            return new AnalysisDefinition
            {
                Behaviours = root.Element("Behaviours").Elements("Behaviour").Select(e => new BehaviourDefinition
                {
                    Id = (string)e.Attribute("id"),
                    Polarity = (string)e.Attribute("polarity"),
                    Rapport = (int)e.Attribute("rapport"),
                    Criterion = (string)e.Attribute("criterion"),
                    AutoMaxWords = (int?)e.Attribute("autoMaxWords") ?? 0,
                    Description = Text(e, "Description"),
                    Examples = e.Elements("Example").Select(x => x.Value.Trim()).ToList(),
                }).ToList(),
                Scoring = new AnalysisScoring
                {
                    MaxTurnChange = (int?)scoring.Attribute("maxTurnChange") ?? 15,
                    TriggerWell = (int)scoring.Element("Trigger").Attribute("well"),
                    TriggerRepeatWell = (int?)scoring.Element("Trigger").Attribute("repeatWell") ?? 0,
                    TriggerBadly = (int)scoring.Element("Trigger").Attribute("badly"),
                    BoundaryLow = (int)scoring.Element("Boundary").Attribute("low"),
                    BoundaryMedium = (int)scoring.Element("Boundary").Attribute("medium"),
                    BoundaryHigh = (int)scoring.Element("Boundary").Attribute("high"),
                    CueNoticed = (int)scoring.Element("Cue").Attribute("noticed"),
                    CueMissed = (int)scoring.Element("Cue").Attribute("missed"),
                },
                Moods = new MoodNotes
                {
                    Cold = Text(moods, "Cold"),
                    Neutral = Text(moods, "Neutral"),
                    Warm = Text(moods, "Warm"),
                    LastWarning = Text(moods, "LastWarning"),
                    Leave = Text(moods, "Leave"),
                },
            };
        }

        public static List<CharacterDefinition> LoadCharacters(string dataRoot, List<string> errors)
        {
            var characters = new List<CharacterDefinition>();
            string dir = Path.Combine(dataRoot, "Characters");
            if (!Directory.Exists(dir))
            {
                errors.Add($"Character folder not found: {dir}");
                return characters;
            }

            string xsd = Schema(dataRoot, "character.xsd");
            foreach (string file in Directory.GetFiles(dir, "*.xml").OrderBy(f => f))
            {
                XDocument doc = LoadValidated(file, xsd, errors);
                if (doc == null)
                    continue;

                CharacterDefinition character = ReadCharacter(doc.Root);
                character.SourceFile = Path.GetFileName(file);

                if (!CheckRules(character, errors))
                    continue;
                if (characters.Any(c => c.Id == character.Id))
                {
                    errors.Add($"{character.SourceFile}: duplicate character id '{character.Id}', skipped.");
                    continue;
                }
                characters.Add(character);
            }
            return characters;
        }

        static string Schema(string dataRoot, string file)
        {
            return Path.Combine(dataRoot, "Schemas", file);
        }

        // References between files, which a single-document XSD cannot check.
        static void CheckCrossReferences(GameData data, List<string> errors)
        {
            foreach (CharacterDefinition c in data.Characters)
            {
                foreach (string id in c.BehaviourOverrides.Keys.ToList())
                {
                    if (data.Analysis.FindBehaviour(id) != null)
                        continue;
                    errors.Add($"{c.SourceFile}: override for unknown behaviour '{id}' (not in analysis.xml), ignored.");
                    c.BehaviourOverrides.Remove(id);
                }

                var criteria = new HashSet<string>(c.Rubric.Criteria.Select(cr => cr.Id));
                foreach (BehaviourDefinition b in data.Analysis.Behaviours)
                {
                    if (b.Criterion != null && !criteria.Contains(b.Criterion))
                        DialogLog.Warning($"{c.SourceFile}: rubric has no criterion '{b.Criterion}', behaviour '{b.Id}' will not count towards a criterion.");
                }
            }
        }

        // Returns null (and adds to errors) if the file is missing, malformed or breaks the schema.
        static XDocument LoadValidated(string xmlPath, string xsdPath, List<string> errors)
        {
            string fileName = Path.GetFileName(xmlPath);
            if (!File.Exists(xmlPath))
            {
                errors.Add($"Missing file: {xmlPath}");
                return null;
            }

            var schemaErrors = new List<string>();
            var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema };
            settings.Schemas.Add(null, xsdPath);
            settings.ValidationEventHandler += (sender, e) =>
                schemaErrors.Add($"{fileName}:{e.Exception?.LineNumber}: {e.Message}");

            try
            {
                XDocument doc;
                using (XmlReader reader = XmlReader.Create(xmlPath, settings))
                    doc = XDocument.Load(reader, LoadOptions.SetLineInfo);

                if (schemaErrors.Count > 0)
                {
                    errors.AddRange(schemaErrors);
                    return null;
                }
                return doc;
            }
            catch (Exception e) when (e is XmlException || e is XmlSchemaException || e is IOException)
            {
                errors.Add($"{fileName}: {e.Message}");
                return null;
            }
        }

        // Checks the schema cannot express (XSD 1.0 has no cross-attribute assertions).
        static bool CheckRules(CharacterDefinition c, List<string> errors)
        {
            bool ok = true;
            if (c.Rules.MinTurns > c.Rules.MaxTurns)
            {
                errors.Add($"{c.SourceFile}: minTurns ({c.Rules.MinTurns}) is greater than maxTurns ({c.Rules.MaxTurns}).");
                ok = false;
            }

            if (c.Rapport.ColdBelow >= c.Rapport.WarmAbove)
            {
                errors.Add($"{c.SourceFile}: RapportRules coldBelow ({c.Rapport.ColdBelow}) must be lower than warmAbove ({c.Rapport.WarmAbove}).");
                ok = false;
            }

            float weightSum = c.Rubric.Criteria.Sum(cr => cr.Weight);
            if (Math.Abs(weightSum - 1f) > 0.01f)
            {
                errors.Add($"{c.SourceFile}: rubric weights add up to {weightSum:0.00}, expected 1.00.");
                ok = false;
            }
            return ok;
        }

        static CharacterDefinition ReadCharacter(XElement root)
        {
            var c = new CharacterDefinition
            {
                Id = (string)root.Attribute("id"),
                Language = (string)root.Attribute("lang") ?? "en",
                Difficulty = (string)root.Attribute("difficulty"),
                Name = Text(root, "Name"),
                Age = (int)root.Element("Age"),
                Occupation = Text(root, "Occupation"),
                Background = Text(root, "Background"),
                SecretGoal = Text(root.Element("Goals"), "SecretGoal"),
                Greeting = Text(root, "Greeting"),
            };

            XElement personality = root.Element("Personality");
            XElement ocean = personality.Element("OCEAN");
            c.Ocean = new Ocean
            {
                Openness = Float(ocean, "O"),
                Conscientiousness = Float(ocean, "C"),
                Extraversion = Float(ocean, "E"),
                Agreeableness = Float(ocean, "A"),
                Neuroticism = Float(ocean, "N"),
            };
            c.PersonalityDescription = Text(personality, "Description");

            XElement style = root.Element("CommunicationStyle");
            c.Style = new CommunicationStyle
            {
                Directness = (string)style.Attribute("directness"),
                Humor = (string)style.Attribute("humor"),
                Verbosity = (string)style.Attribute("verbosity"),
                BoundaryStrictness = (string)style.Attribute("boundaries"),
                Description = style.Value.Trim(),
            };

            XElement interests = root.Element("Interests");
            c.Likes = interests.Elements("Like").Select(e => e.Value.Trim()).ToList();
            c.Dislikes = interests.Elements("Dislike").Select(e => e.Value.Trim()).ToList();

            c.Boundaries = root.Element("Boundaries").Elements("Boundary").Select(e => new Boundary
            {
                Id = (string)e.Attribute("id"),
                Severity = (string)e.Attribute("severity") ?? "medium",
                Description = e.Value.Trim(),
            }).ToList();

            XElement triggers = root.Element("Triggers");
            if (triggers != null)
            {
                c.Triggers = triggers.Elements("Trigger").Select(e => new Trigger
                {
                    Id = (string)e.Attribute("id"),
                    Kind = (string)e.Attribute("kind"),
                    Effect = (string)e.Attribute("effect"),
                    Strength = (int?)e.Attribute("strength") ?? 1,
                    Topics = Text(e, "Topics"),
                    Reaction = Text(e, "Reaction"),
                }).ToList();
            }

            XElement cues = root.Element("EmotionalCues");
            if (cues != null)
            {
                c.EmotionalCues = cues.Elements("Cue").Select(e => new EmotionalCue
                {
                    Id = (string)e.Attribute("id"),
                    Topic = (string)e.Attribute("topic"),
                    Description = e.Value.Trim(),
                }).ToList();
            }

            XElement rules = root.Element("ConversationRules");
            c.Rules = new ConversationRules
            {
                MinTurns = (int)rules.Attribute("minTurns"),
                MaxTurns = (int)rules.Attribute("maxTurns"),
                MaxBoundaryStrikes = (int?)rules.Attribute("maxBoundaryStrikes") ?? 2,
            };

            XElement rapport = root.Element("RapportRules");
            c.Rapport = new RapportRules
            {
                Start = (int?)rapport?.Attribute("start") ?? 40,
                ColdBelow = (int?)rapport?.Attribute("coldBelow") ?? 25,
                WarmAbove = (int?)rapport?.Attribute("warmAbove") ?? 65,
            };

            XElement overrides = root.Element("BehaviourOverrides");
            if (overrides != null)
            {
                foreach (XElement o in overrides.Elements("Override"))
                    c.BehaviourOverrides[(string)o.Attribute("behaviour")] = (int)o.Attribute("rapport");
            }

            XElement rubric = root.Element("EvaluationRubric");
            c.Rubric = new EvaluationRubric
            {
                TotalScore = (int?)rubric.Attribute("totalScore") ?? 100,
                Criteria = rubric.Elements("Criterion").Select(e => new Criterion
                {
                    Id = (string)e.Attribute("id"),
                    Label = (string)e.Attribute("label"),
                    Weight = Float(e, "weight"),
                    Scale = (string)e.Attribute("scale") ?? "1-5",
                    Description = Text(e, "Description"),
                }).ToList(),
            };

            return c;
        }

        static ModelRole ReadModelRole(XElement e)
        {
            return new ModelRole
            {
                Model = (string)e.Attribute("model"),
                Temperature = Float(e, "temperature"),
                ContextLength = (int?)e.Attribute("contextLength") ?? 8192,
                MaxTokens = (int?)e.Attribute("maxTokens") ?? 256,
            };
        }

        static string Text(XElement parent, string name)
        {
            return parent?.Element(name)?.Value.Trim();
        }

        // Invariant culture: a Bulgarian Windows locale would otherwise expect "0,8".
        static float Float(XElement e, string attribute)
        {
            return float.Parse((string)e.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
