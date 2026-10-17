using System;
using System.Collections.Generic;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The popups that open when the pointer rests on a widget (the weather's forecast, the CPU temperature's graph): one at a time. When one
    /// is asked for while another is open, the other slides away first, and the one asked for slides in when it has gone - not both at once,
    /// one over the other.
    /// </summary>
    public static class HoverPopups
    {
        private static PopupSlide _open;
        private static (PopupSlide Slide, Action Show)? _waiting;
        private static PopupSlide _waitingFor;
        private static readonly HashSet<PopupSlide> Known = new();

        /// <summary>The delay before a popup opens when another one is open: the pointer went from one widget to the other, so it is quick.</summary>
        public static readonly TimeSpan SwitchDelay = TimeSpan.FromMilliseconds(30);

        /// <summary>True while one of the popups is open (sliding in or away included).</summary>
        public static bool AnyOpen => _open != null && _open.IsOpen;

        /// <summary>Opens the popup of a slide (show does it: places it and calls Show), after the one that is open has slid away.</summary>
        public static void Open(PopupSlide slide, Action show)
        {
            if (Known.Add(slide))
            {
                slide.Closed += () => OnClosed(slide);
            }

            if (_open != null && _open != slide && _open.IsOpen)
            {
                // The last one asked for is the one that opens when the other has gone.
                _waiting = (slide, show);
                _waitingFor = _open;
                _open.Hide();
                return;
            }

            _waiting = null;
            _open = slide;
            show();
        }

        /// <summary>
        /// For a popup that is not one of these (the media popup, opened by a click): if one of them is open it slides away first, and show
        /// runs when it has gone, so they are not over one another.
        /// </summary>
        public static void CloseThen(Action show)
        {
            if (_open != null && _open.IsOpen)
            {
                _waiting = (null, show);
                _waitingFor = _open;
                _open.Hide(PopupSlide.QuickHideTime);
                return;
            }

            show();
        }

        private static void OnClosed(PopupSlide closed)
        {
            if (_open == closed)
            {
                _open = null;
            }

            if (_waiting is { } next && _waitingFor == closed)
            {
                _waiting = null;
                _waitingFor = null;
                _open = next.Slide;
                next.Show();
            }
        }
    }
}
