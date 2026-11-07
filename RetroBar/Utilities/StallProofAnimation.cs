using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace RetroBar.Utilities
{
    /// <summary>
    /// A number that goes through the values of its key frames in time, as an animation does, but whose time is the time that was drawn: a stretch of
    /// more than 60 ms between two evaluations (the thread was busy - a program opening or closing keeps it so for most of a second) is not counted.
    /// A fade that is 150 ms long was over before the first frame was drawn whenever the stall was as long as that, which is why the fades of a tab
    /// that comes up or goes were missing; this one is seen from its start whatever came before. Its clock runs a few seconds past the animation, which
    /// stays at its last value.
    /// </summary>
    internal sealed class StallProofAnimation : DoubleAnimationBase
    {
        private readonly List<(double Ms, double Value, IEasingFunction Ease)> _frames;
        private readonly double _offsetMs;
        private double _playedMs;
        private long _lastTicks;

        public StallProofAnimation(IEnumerable<(double Ms, double Value, IEasingFunction Ease)> frames, double offsetMs = 0)
        {
            _frames = new List<(double, double, IEasingFunction)>(frames);
            _offsetMs = Math.Max(0, offsetMs);
            double last = _frames.Count > 0 ? _frames[^1].Ms : 0;
            Duration = new Duration(TimeSpan.FromMilliseconds(last + 3000));
        }

        protected override Freezable CreateInstanceCore() => new StallProofAnimation(_frames, _offsetMs);

        protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastTicks != 0)
            {
                double step = (now - _lastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (step <= 60)
                {
                    _playedMs += step;
                }
            }

            _lastTicks = now;
            double t = _offsetMs + _playedMs;
            if (_frames.Count == 0)
            {
                return defaultDestinationValue;
            }

            if (t <= _frames[0].Ms)
            {
                return _frames[0].Value;
            }

            for (int i = 1; i < _frames.Count; i++)
            {
                if (t < _frames[i].Ms)
                {
                    double span = _frames[i].Ms - _frames[i - 1].Ms;
                    double progress = span <= 0 ? 1 : (t - _frames[i - 1].Ms) / span;
                    double eased = _frames[i].Ease?.Ease(progress) ?? progress;
                    return _frames[i - 1].Value + (_frames[i].Value - _frames[i - 1].Value) * eased;
                }
            }

            return _frames[^1].Value;
        }
    }
}
