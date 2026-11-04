using System;
using System.Collections.Generic;

namespace Dialog
{
    // How the character currently feels about the player (0..100) and how many times
    // the player crossed a boundary. Pure arithmetic over the analyzer's labels;
    // every number comes from analysis.xml or the character's XML.
    public class RapportModel
    {
        public const string Cold = "cold";
        public const string Neutral = "neutral";
        public const string Warm = "warm";

        public float Value { get; private set; }
        public int Strikes { get; private set; }
        public int MaxStrikes => character.Rules.MaxBoundaryStrikes;
        public bool StruckOut => Strikes >= MaxStrikes;

        public string Band => Value < character.Rapport.ColdBelow ? Cold
            : Value > character.Rapport.WarmAbove ? Warm
            : Neutral;

        readonly CharacterDefinition character;
        readonly AnalysisDefinition analysis;
        readonly HashSet<string> rewardedTriggers = new HashSet<string>();

        public RapportModel(CharacterDefinition character, AnalysisDefinition analysis)
        {
            this.character = character;
            this.analysis = analysis;
            Value = character.Rapport.Start;
        }

        // Applies one analysed turn, fills its Delta/Breakdown fields and returns the delta.
        public int Apply(TurnAnalysis turn)
        {
            AnalysisScoring scoring = analysis.Scoring;
            int delta = 0;

            foreach (BehaviourHit hit in turn.Behaviours)
            {
                int points = PointsFor(hit.Tag);
                if (points != 0)
                    turn.Breakdown.Add($"{hit.Tag} {points:+0;-0}");
                delta += points;
            }

            foreach (TriggerHit hit in turn.Triggers)
            {
                Trigger trigger = character.Triggers.Find(t => t.Id == hit.Id);
                // Interest pays off once; mishandling a sore spot hurts every time.
                bool repeat = hit.HandledWell && !rewardedTriggers.Add(hit.Id);
                int perStrength = !hit.HandledWell ? scoring.TriggerBadly
                    : repeat ? scoring.TriggerRepeatWell
                    : scoring.TriggerWell;
                int points = trigger.Strength * perStrength;
                if (points != 0)
                    turn.Breakdown.Add($"trigger {hit.Id} ({(hit.HandledWell ? "well" : "badly")}) {points:+0;-0}");
                delta += points;
            }

            if (turn.BoundaryCrossed != null)
            {
                Boundary boundary = character.Boundaries.Find(b => b.Id == turn.BoundaryCrossed);
                int points = boundary.Severity == "high" ? scoring.BoundaryHigh
                    : boundary.Severity == "low" ? scoring.BoundaryLow
                    : scoring.BoundaryMedium;
                turn.Breakdown.Add($"boundary {boundary.Id} {points:+0;-0} (strike)");
                turn.Strike = true;
                Strikes++;
                delta += points;
            }

            if (turn.Cue == "noticed" || turn.Cue == "missed")
            {
                int points = turn.Cue == "noticed" ? scoring.CueNoticed : scoring.CueMissed;
                turn.Breakdown.Add($"cue {turn.Cue} {points:+0;-0}");
                delta += points;
            }

            delta = Math.Max(-scoring.MaxTurnChange, Math.Min(scoring.MaxTurnChange, delta));
            float before = Value;
            Value = Math.Max(0f, Math.Min(100f, Value + delta));

            turn.RapportDelta = (int)Math.Round(Value - before);
            turn.RapportAfter = Value;
            return turn.RapportDelta;
        }

        public int PointsFor(string behaviourId)
        {
            if (character.BehaviourOverrides.TryGetValue(behaviourId, out int points))
                return points;
            BehaviourDefinition behaviour = analysis.FindBehaviour(behaviourId);
            return behaviour != null ? behaviour.Rapport : 0;
        }

        // Hidden note for the Actor LLM describing how the character feels right now.
        public string MoodNote()
        {
            MoodNotes moods = analysis.Moods;
            string note = Band == Cold ? moods.Cold : Band == Warm ? moods.Warm : moods.Neutral;
            if (Strikes > 0 && Strikes == MaxStrikes - 1)
                note += " " + moods.LastWarning;
            return note;
        }
    }
}
