using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using UnityEngine;

namespace Dialog
{
    // Reads the game's XML data from StreamingAssets/Dialog, validating every file
    // against its XSD first. Invalid files are skipped and reported, never half-loaded.
    public static class XmlDataLoader
    {
        public static string DataRoot => Path.Combine(Application.streamingAssetsPath, "Dialog");
        static string SchemaDir => Path.Combine(DataRoot, "Schemas");

        public static GameSettings LoadSettings(List<string> errors)
        {
            string path = Path.Combine(DataRoot, "settings.xml");
            XDocument doc = LoadValidated(path, Path.Combine(SchemaDir, "settings.xsd"), errors);
            if (doc == null)
                return null;

            XElement root = doc.Root;
            XElement ollama = root.Element("Ollama");
            return new GameSettings
            {
                OllamaUrl = ((string)ollama.Attribute("url")).TrimEnd('/'),
                KeepAlive = (string)ollama.Attribute("keepAlive") ?? "30m",
                Actor = ReadModelRole(root.Element("Actor")),
                Evaluator = ReadModelRole(root.Element("Evaluator")),
            };
        }

        public static List<CharacterDefinition> LoadCharacters(List<string> errors)
        {
            var characters = new List<CharacterDefinition>();
            string dir = Path.Combine(DataRoot, "Characters");
            if (!Directory.Exists(dir))
            {
                errors.Add($"Character folder not found: {dir}");
                return characters;
            }

            string xsd = Path.Combine(SchemaDir, "character.xsd");
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

            float weightSum = c.Rubric.Criteria.Sum(cr => cr.Weight);
            if (Mathf.Abs(weightSum - 1f) > 0.01f)
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
