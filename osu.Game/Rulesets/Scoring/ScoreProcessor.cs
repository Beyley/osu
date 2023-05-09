﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using osu.Game.Beatmaps;
using osu.Game.Extensions;
using osu.Game.Localisation;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Replays;
using osu.Game.Scoring;

namespace osu.Game.Rulesets.Scoring
{
    public abstract partial class ScoreProcessor : JudgementProcessor
    {
        protected const double MAX_SCORE = 1000000;

        private const double accuracy_cutoff_x = 1;
        private const double accuracy_cutoff_s = 0.95;
        private const double accuracy_cutoff_a = 0.9;
        private const double accuracy_cutoff_b = 0.8;
        private const double accuracy_cutoff_c = 0.7;
        private const double accuracy_cutoff_d = 0;

        /// <summary>
        /// Invoked when this <see cref="ScoreProcessor"/> was reset from a replay frame.
        /// </summary>
        public event Action? OnResetFromReplayFrame;

        /// <summary>
        /// The current total score.
        /// </summary>
        public readonly BindableLong TotalScore = new BindableLong { MinValue = 0 };

        /// <summary>
        /// The current accuracy.
        /// </summary>
        public readonly BindableDouble Accuracy = new BindableDouble(1) { MinValue = 0, MaxValue = 1 };

        /// <summary>
        /// The minimum achievable accuracy for the whole beatmap at this stage of gameplay.
        /// Assumes that all objects that have not been judged yet will receive the minimum hit result.
        /// </summary>
        public readonly BindableDouble MinimumAccuracy = new BindableDouble { MinValue = 0, MaxValue = 1 };

        /// <summary>
        /// The maximum achievable accuracy for the whole beatmap at this stage of gameplay.
        /// Assumes that all objects that have not been judged yet will receive the maximum hit result.
        /// </summary>
        public readonly BindableDouble MaximumAccuracy = new BindableDouble(1) { MinValue = 0, MaxValue = 1 };

        /// <summary>
        /// The current combo.
        /// </summary>
        public readonly BindableInt Combo = new BindableInt();

        /// <summary>
        /// The current selected mods
        /// </summary>
        public readonly Bindable<IReadOnlyList<Mod>> Mods = new Bindable<IReadOnlyList<Mod>>(Array.Empty<Mod>());

        /// <summary>
        /// The current rank.
        /// </summary>
        public readonly Bindable<ScoreRank> Rank = new Bindable<ScoreRank>(ScoreRank.X);

        /// <summary>
        /// The highest combo achieved by this score.
        /// </summary>
        public readonly BindableInt HighestCombo = new BindableInt();

        /// <summary>
        /// The <see cref="ScoringMode"/> used to calculate scores.
        /// </summary>
        public readonly Bindable<ScoringMode> Mode = new Bindable<ScoringMode>();

        /// <summary>
        /// The <see cref="HitEvent"/>s collected during gameplay thus far.
        /// Intended for use with various statistics displays.
        /// </summary>
        public IReadOnlyList<HitEvent> HitEvents => hitEvents;

        /// <summary>
        /// An arbitrary multiplier to scale scores in the <see cref="ScoringMode.Classic"/> scoring mode.
        /// </summary>
        protected virtual double ClassicScoreMultiplier => 36;

        /// <summary>
        /// The ruleset this score processor is valid for.
        /// </summary>
        public readonly Ruleset Ruleset;

        /// <summary>
        /// The sum of all basic judgements at the current time.
        /// </summary>
        private double currentBasicScore;

        /// <summary>
        /// The maximum sum of basic judgements at the current time.
        /// </summary>
        private double currentMaxBasicScore;

        /// <summary>
        /// The total count of basic judgements in the beatmap.
        /// </summary>
        protected int MaxBasicJudgements { get; private set; }

        /// <summary>
        /// The current count of basic judgements by the player.
        /// </summary>
        protected int CurrentBasicJudgements { get; private set; }

        /// <summary>
        /// The current combo score.
        /// </summary>
        protected double ComboPortion { get; set; }

        /// <summary>
        /// The maximum achievable combo score.
        /// </summary>
        protected double MaxComboPortion { get; private set; }

        /// <summary>
        /// The current bonus score.
        /// </summary>
        protected double BonusPortion { get; set; }

        /// <summary>
        /// The total score multiplier.
        /// </summary>
        protected double ScoreMultiplier { get; private set; } = 1;

        public Dictionary<HitResult, int> MaximumStatistics
        {
            get
            {
                if (!beatmapApplied)
                    throw new InvalidOperationException($"Cannot access maximum statistics before calling {nameof(ApplyBeatmap)}.");

                return new Dictionary<HitResult, int>(maximumResultCounts);
            }
        }

        private bool beatmapApplied;

        private readonly Dictionary<HitResult, int> scoreResultCounts = new Dictionary<HitResult, int>();
        private readonly Dictionary<HitResult, int> maximumResultCounts = new Dictionary<HitResult, int>();

        private readonly List<HitEvent> hitEvents = new List<HitEvent>();
        private HitObject? lastHitObject;

        protected ScoreProcessor(Ruleset ruleset)
        {
            Ruleset = ruleset;

            Combo.ValueChanged += combo => HighestCombo.Value = Math.Max(HighestCombo.Value, combo.NewValue);
            Accuracy.ValueChanged += accuracy =>
            {
                Rank.Value = RankFromAccuracy(accuracy.NewValue);
                foreach (var mod in Mods.Value.OfType<IApplicableToScoreProcessor>())
                    Rank.Value = mod.AdjustRank(Rank.Value, accuracy.NewValue);
            };

            Mode.ValueChanged += _ => updateScore();
            Mods.ValueChanged += mods =>
            {
                ScoreMultiplier = 1;

                foreach (var m in mods.NewValue)
                    ScoreMultiplier *= m.ScoreMultiplier;

                updateScore();
            };
        }

        public override void ApplyBeatmap(IBeatmap beatmap)
        {
            base.ApplyBeatmap(beatmap);
            beatmapApplied = true;
        }

        protected sealed override void ApplyResultInternal(JudgementResult result)
        {
            result.ComboAtJudgement = Combo.Value;
            result.HighestComboAtJudgement = HighestCombo.Value;

            if (result.FailedAtJudgement)
                return;

            scoreResultCounts[result.Type] = scoreResultCounts.GetValueOrDefault(result.Type) + 1;

            if (!result.Type.IsScorable())
                return;

            if (result.Type.IncreasesCombo())
                Combo.Value++;
            else if (result.Type.BreaksCombo())
                Combo.Value = 0;

            if (result.Type.IsBasic())
                CurrentBasicJudgements++;

            currentMaxBasicScore += Judgement.ToNumericResult(result.Judgement.MaxResult);
            currentBasicScore += Judgement.ToNumericResult(result.Type);

            AddScoreChange(result);

            hitEvents.Add(CreateHitEvent(result));
            lastHitObject = result.HitObject;

            updateScore();
        }

        /// <summary>
        /// Creates the <see cref="HitEvent"/> that describes a <see cref="JudgementResult"/>.
        /// </summary>
        /// <param name="result">The <see cref="JudgementResult"/> to describe.</param>
        /// <returns>The <see cref="HitEvent"/>.</returns>
        protected virtual HitEvent CreateHitEvent(JudgementResult result)
            => new HitEvent(result.TimeOffset, result.Type, result.HitObject, lastHitObject, null);

        protected sealed override void RevertResultInternal(JudgementResult result)
        {
            Combo.Value = result.ComboAtJudgement;
            HighestCombo.Value = result.HighestComboAtJudgement;

            if (result.FailedAtJudgement)
                return;

            scoreResultCounts[result.Type] = scoreResultCounts.GetValueOrDefault(result.Type) - 1;

            if (!result.Type.IsScorable())
                return;

            if (result.Type.IsBasic())
                CurrentBasicJudgements--;

            currentMaxBasicScore -= Judgement.ToNumericResult(result.Judgement.MaxResult);
            currentBasicScore -= Judgement.ToNumericResult(result.Type);

            RemoveScoreChange(result);

            Debug.Assert(hitEvents.Count > 0);
            lastHitObject = hitEvents[^1].LastHitObject;
            hitEvents.RemoveAt(hitEvents.Count - 1);

            updateScore();
        }

        protected virtual void AddScoreChange(JudgementResult result)
        {
            if (result.Type.IsBonus())
                BonusPortion += Judgement.ToNumericResult(result.Type);
            else
                ComboPortion += Judgement.ToNumericResult(result.Type) * (1 + result.ComboAtJudgement / 10d);
        }

        protected virtual void RemoveScoreChange(JudgementResult result)
        {
            if (result.Type.IsBonus())
                BonusPortion -= Judgement.ToNumericResult(result.Type);
            else
                ComboPortion -= Judgement.ToNumericResult(result.Type) * (1 + result.ComboAtJudgement / 10d);
        }

        private void updateScore()
        {
            Accuracy.Value = currentMaxBasicScore > 0 ? currentBasicScore / currentMaxBasicScore : 1;

            double standardisedScore = ComputeTotalScore();

            if (Mode.Value == ScoringMode.Standardised)
                TotalScore.Value = (long)Math.Round(standardisedScore);
            else
                TotalScore.Value = ConvertToClassic(standardisedScore);
        }

        public long ConvertToClassic(double standardised)
        {
            // This gives a similar feeling to osu!stable scoring (ScoreV1) while keeping classic scoring as only a constant multiple of standardised scoring.
            // The invariant is important to ensure that scores don't get re-ordered on leaderboards between the two scoring modes.
            double scaledRawScore = standardised / MAX_SCORE;
            return (long)Math.Round(Math.Pow(scaledRawScore * Math.Max(1, MaxBasicJudgements), 2) * ClassicScoreMultiplier);
        }

        protected abstract double ComputeTotalScore();

        /// <summary>
        /// Resets this ScoreProcessor to a default state.
        /// </summary>
        /// <param name="storeResults">Whether to store the current state of the <see cref="ScoreProcessor"/> for future use.</param>
        protected override void Reset(bool storeResults)
        {
            base.Reset(storeResults);

            hitEvents.Clear();
            lastHitObject = null;

            if (storeResults)
            {
                MaxComboPortion = ComboPortion;
                MaxBasicJudgements = CurrentBasicJudgements;

                maximumResultCounts.Clear();
                maximumResultCounts.AddRange(scoreResultCounts);
            }

            scoreResultCounts.Clear();

            currentBasicScore = 0;
            currentMaxBasicScore = 0;
            CurrentBasicJudgements = 0;
            ComboPortion = 0;
            BonusPortion = 0;

            TotalScore.Value = 0;
            Accuracy.Value = 1;
            Combo.Value = 0;
            Rank.Disabled = false;
            Rank.Value = ScoreRank.X;
            HighestCombo.Value = 0;

            currentBasicScore = 0;
            currentMaxBasicScore = 0;
        }

        /// <summary>
        /// Retrieve a score populated with data for the current play this processor is responsible for.
        /// </summary>
        public virtual void PopulateScore(ScoreInfo score)
        {
            score.Combo = Combo.Value;
            score.MaxCombo = HighestCombo.Value;
            score.Accuracy = Accuracy.Value;
            score.Rank = Rank.Value;
            score.HitEvents = hitEvents;
            score.Statistics.Clear();
            score.MaximumStatistics.Clear();

            foreach (var result in HitResultExtensions.ALL_TYPES)
                score.Statistics[result] = scoreResultCounts.GetValueOrDefault(result);

            foreach (var result in HitResultExtensions.ALL_TYPES)
                score.MaximumStatistics[result] = maximumResultCounts.GetValueOrDefault(result);

            // Populate total score after everything else.
            score.TotalScore = TotalScore.Value;
        }

        /// <summary>
        /// Populates a failed score, marking it with the <see cref="ScoreRank.F"/> rank.
        /// </summary>
        public void FailScore(ScoreInfo score)
        {
            if (Rank.Value == ScoreRank.F)
                return;

            score.Passed = false;
            Rank.Value = ScoreRank.F;

            PopulateScore(score);
        }

        public override void ResetFromReplayFrame(ReplayFrame frame)
        {
            base.ResetFromReplayFrame(frame);

            if (frame.Header == null)
                return;

            Combo.Value = frame.Header.Combo;
            HighestCombo.Value = frame.Header.MaxCombo;

            scoreResultCounts.Clear();
            scoreResultCounts.AddRange(frame.Header.Statistics);

            updateScore();

            OnResetFromReplayFrame?.Invoke();
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            hitEvents.Clear();
        }

        #region Static helper methods

        /// <summary>
        /// Given an accuracy (0..1), return the correct <see cref="ScoreRank"/>.
        /// </summary>
        public static ScoreRank RankFromAccuracy(double accuracy)
        {
            if (accuracy == accuracy_cutoff_x)
                return ScoreRank.X;
            if (accuracy >= accuracy_cutoff_s)
                return ScoreRank.S;
            if (accuracy >= accuracy_cutoff_a)
                return ScoreRank.A;
            if (accuracy >= accuracy_cutoff_b)
                return ScoreRank.B;
            if (accuracy >= accuracy_cutoff_c)
                return ScoreRank.C;

            return ScoreRank.D;
        }

        /// <summary>
        /// Given a <see cref="ScoreRank"/>, return the cutoff accuracy (0..1).
        /// Accuracy must be greater than or equal to the cutoff to qualify for the provided rank.
        /// </summary>
        public static double AccuracyCutoffFromRank(ScoreRank rank)
        {
            switch (rank)
            {
                case ScoreRank.X:
                case ScoreRank.XH:
                    return accuracy_cutoff_x;

                case ScoreRank.S:
                case ScoreRank.SH:
                    return accuracy_cutoff_s;

                case ScoreRank.A:
                    return accuracy_cutoff_a;

                case ScoreRank.B:
                    return accuracy_cutoff_b;

                case ScoreRank.C:
                    return accuracy_cutoff_c;

                case ScoreRank.D:
                    return accuracy_cutoff_d;

                default:
                    throw new ArgumentOutOfRangeException(nameof(rank), rank, null);
            }
        }

        #endregion
    }

    public enum ScoringMode
    {
        [LocalisableDescription(typeof(GameplaySettingsStrings), nameof(GameplaySettingsStrings.StandardisedScoreDisplay))]
        Standardised,

        [LocalisableDescription(typeof(GameplaySettingsStrings), nameof(GameplaySettingsStrings.ClassicScoreDisplay))]
        Classic
    }
}
