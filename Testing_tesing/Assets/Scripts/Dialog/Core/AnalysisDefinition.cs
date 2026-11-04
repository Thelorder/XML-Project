using System.Collections.Generic;
using System.Linq;

namespace Dialog
{
    // In-memory form of analysis.xml (see Schemas/analysis.xsd): the behaviours the
    // turn analyzer may report and how many rapport points each finding is worth.
    public class AnalysisDefinition
    {
        public List<BehaviourDefinition> Behaviours = new List<BehaviourDefinition>();
        public AnalysisScoring Scoring;
        public MoodNotes Moods;

        public BehaviourDefinition FindBehaviour(string id)
        {
            return Behaviours.FirstOrDefault(b => b.Id == id);
        }
    }

    public class BehaviourDefinition
    {
        public string Id;
        public string Polarity; // positive | negative | neutral
        public int Rapport;
        public string Criterion;
        // > 0: messages of at most this many words (and not questions) get this behaviour from the game itself.
        public int AutoMaxWords;
        public string Description;
        public List<string> Examples = new List<string>();
    }

    public class AnalysisScoring
    {
        public int MaxTurnChange;
        public int TriggerWell;
        public int TriggerRepeatWell;
        public int TriggerBadly;
        public int BoundaryLow;
        public int BoundaryMedium;
        public int BoundaryHigh;
        public int CueNoticed;
        public int CueMissed;
    }

    public class MoodNotes
    {
        public string Cold;
        public string Neutral;
        public string Warm;
        public string LastWarning;
        public string Leave;
    }
}
