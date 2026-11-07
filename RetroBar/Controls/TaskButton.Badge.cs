using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace RetroBar.Controls
{
    // The badge of a tab in the Windows 7 style (the overlay icon an app puts on its taskbar button: a message count, a status) fades in when
    // the app sets it and out when it takes it away, rather than being there and gone at once.
    public partial class TaskButton
    {
        private static readonly Duration BadgeFade = new Duration(TimeSpan.FromMilliseconds(180));

        private bool _badgeShown;

        // animate: false for the badge a tab has when it is made (a tab that is made again must not show it fading in).
        private void UpdateBadge(bool animate)
        {
            if (Window == null)
            {
                return;
            }

            System.Windows.Media.ImageSource icon = Window.OverlayIcon;

            if (icon != null)
            {
                IconOnlyBadge.Source = icon;
                if (!_badgeShown)
                {
                    _badgeShown = true;
                    FadeBadge(1, animate);
                }

                return;
            }

            if (!_badgeShown)
            {
                IconOnlyBadge.Source = null;
                IconOnlyBadgeFade.Opacity = 0;
                return;
            }

            _badgeShown = false;
            FadeBadge(0, animate);
        }

        private void FadeBadge(double to, bool animate)
        {
            if (!animate)
            {
                IconOnlyBadgeFade.BeginAnimation(OpacityProperty, null);
                IconOnlyBadgeFade.Opacity = to;
                if (to == 0)
                {
                    IconOnlyBadge.Source = null;
                }

                return;
            }

            // From where it is (a badge that comes while the last one is still going out starts from what is seen).
            var animation = new DoubleAnimation(IconOnlyBadgeFade.Opacity, to, BadgeFade) { FillBehavior = FillBehavior.Stop };
            animation.Completed += (s, e) =>
            {
                IconOnlyBadgeFade.Opacity = to;
                if (to == 0 && !_badgeShown)
                {
                    // The picture is kept till it has faded out (it is the one that was there), then let go.
                    IconOnlyBadge.Source = null;
                }
            };

            IconOnlyBadgeFade.BeginAnimation(OpacityProperty, animation);
        }
    }
}
