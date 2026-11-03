using System.Collections.Generic;

namespace Dialog
{
    // In-memory form of a character XML file (see Schemas/character.xsd).
    // Plain data only: filled by CharacterLoader, read by PromptBuilder and the evaluators.
    public class CharacterDefinition
    {
        public string Id;
        public string Language;
        public string Difficulty;
        public string SourceFile;

        public string Name;
        public int Age;
        public string Occupation;
        public string Background;

        public Ocean Ocean;
        public string PersonalityDescription;

        public CommunicationStyle Style;

        public List<string> Likes = new List<string>();
        public List<string> Dislikes = new List<string>();
        public List<Boundary> Boundaries = new List<Boundary>();
        public List<Trigger> Triggers = new List<Trigger>();
        public List<EmotionalCue> EmotionalCues = new List<EmotionalCue>();

        public string SecretGoal;
        public string Greeting;
        public ConversationRules Rules;
        public EvaluationRubric Rubric;
    }

    // Big Five traits, each 0..1.
    public struct Ocean
    {
        public float Openness;
        public float Conscientiousness;
        public float Extraversion;
        public float Agreeableness;
        public float Neuroticism;
    }

    public class CommunicationStyle
    {
        public string Directness; // low | medium | high
        public string Humor;      // low | medium | high
        public string Verbosity;  // low | medium | high
        public string BoundaryStrictness; // relaxed | normal | strict
        public string Description;
    }

    public class Boundary
    {
        public string Id;
        public string Severity;
        public string Description;
    }

    public class Trigger
    {
        public string Id;
        public string Kind;   // interest | sensitive
        public string Effect; // warm | cold
        public int Strength;  // 1..3
        public string Topics;
        public string Reaction;
    }

    public class EmotionalCue
    {
        public string Id;
        public string Topic;
        public string Description;
    }

    public class ConversationRules
    {
        public int MinTurns;
        public int MaxTurns;
        public int MaxBoundaryStrikes;
    }

    public class EvaluationRubric
    {
        public int TotalScore;
        public List<Criterion> Criteria = new List<Criterion>();
    }

    public class Criterion
    {
        public string Id;
        public string Label;
        public float Weight;
        public string Scale;
        public string Description;
    }
}
