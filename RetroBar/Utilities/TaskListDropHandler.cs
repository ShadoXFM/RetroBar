using GongSolutions.Wpf.DragDrop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Drag-to-rearrange for the taskbar's tabs: the library's normal drop (which moves the one item
    /// in the underlying list - the other tabs aren't touched), plus a slide: every tab's position is
    /// noted before the drop and, once the new layout is in, each tab animates from where it was to
    /// where it is now, so the tabs glide into place instead of jumping.
    /// </summary>
    public class TaskListDropHandler : IDropTarget
    {
        private readonly ItemsControl _list;

        public TaskListDropHandler(ItemsControl list)
        {
            _list = list;
        }

        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.DragOver(dropInfo);

            // The library's insertion mark is made again at every mouse move and flashes: ours is one line, moved (see DropIndicator).
            if (dropInfo.Effects != System.Windows.DragDropEffects.None)
            {
                dropInfo.DropTargetAdorner = null;
                DropIndicator.Show(_list, dropInfo.InsertIndex);
            }
            else
            {
                DropIndicator.Hide(_list);
            }
        }

#if NETCOREAPP3_1_OR_GREATER
        public void DragLeave(IDropInfo dropInfo)
        {
            DropIndicator.Hide(_list);
        }
#endif

#if !NETCOREAPP3_1_OR_GREATER
        public void DragEnter(IDropInfo dropInfo)
        {
            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.DragEnter(dropInfo);
        }

        public void DragLeave(IDropInfo dropInfo)
        {
            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.DragLeave(dropInfo);
        }
#endif

        void IDropTarget.Drop(IDropInfo dropInfo)
        {
            DropIndicator.Hide(_list);
            Dictionary<object, Point> before = ItemSlideAnimation.Capture(_list);

            // The dragged tab's button gets recreated by the move; don't let it grow in from nothing.
            RetroBar.Controls.TaskButton.SuppressSlideInUntilUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);

            GongSolutions.Wpf.DragDrop.DragDrop.DefaultDropHandler.Drop(dropInfo);

            // The tab stays where it was dropped until the list is sorted again (see TaskOpenOrderComparer); nothing is kept.
            if (_list.ItemsSource is System.Windows.Data.ListCollectionView view &&
                view.SourceCollection is System.Collections.ObjectModel.ObservableCollection<ManagedShell.WindowsTasks.ApplicationWindow> source)
            {
                if (Settings.Instance.GroupTaskWindows)
                {
                    TaskOpenOrderComparer.KeepAppWindowsTogether(source, dropInfo.Data as ManagedShell.WindowsTasks.ApplicationWindow);
                }

                // A tab of a pinned program dropped in another place moves its pin there: the order of the tabs is the order of the pins.
                if (dropInfo.Data is ManagedShell.WindowsTasks.ApplicationWindow dragged &&
                    PinnedApps.IndexOf(dragged, PinnedApps.Get()) >= 0)
                {
                    PinnedApps.SaveOrder(PinnedApps.OrderByTabs(view.OfType<ManagedShell.WindowsTasks.ApplicationWindow>()));
                }
            }

            // After the new layout is in, slide each tab from its old position to its new one.
            ItemSlideAnimation.Play(_list, before);
        }
    }
}
