using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.ShellFolders;
using ManagedShell.ShellFolders.Enums;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for Toolbar.xaml
    /// </summary>
    public partial class Toolbar : UserControl
    {
        private bool _ignoreNextUpdate;
        private bool _isLoaded;

        private enum MenuItem : uint
        {
            OpenParentFolder = CommonContextMenuItem.Paste + 1
        }

        public static DependencyProperty PathProperty = DependencyProperty.Register(nameof(Path), typeof(string), typeof(Toolbar), new PropertyMetadata(OnPathChanged));

        public string Path
        {
            get => (string)GetValue(PathProperty);
            set
            {
                SetValue(PathProperty, value);
                SetupFolder(value);
            }
        }

        private static DependencyProperty FolderProperty = DependencyProperty.Register(nameof(Folder), typeof(ShellFolder), typeof(Toolbar));

        public static DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(Taskbar), typeof(Toolbar), new PropertyMetadata(HostChangedCallback));

        public Taskbar Host
        {
            get { return (Taskbar)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        public ToolbarDropHandler DropHandler { get; set; }

        private ShellFolder Folder
        {
            get => (ShellFolder)GetValue(FolderProperty);
            set
            {
                SetValue(FolderProperty, value);
                SetItemsSource();
            }
        }

        public Toolbar()
        {
            DropHandler = new ToolbarDropHandler(this);

            InitializeComponent();
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.QuickLaunchOrder))
            {
                if (_ignoreNextUpdate)
                {
                    _ignoreNextUpdate = false;
                    return;
                }

                Refresh();
            }
            else if (e.PropertyName == nameof(Settings.TaskbarScale))
            {
                Refresh();
            }
        }

        private void Refresh()
        {
            if (Folder == null)
            {
                return;
            }

            ListCollectionView cvs = (ListCollectionView)CollectionViewSource.GetDefaultView(Folder.Files);
            cvs.Refresh();
        }

        private void SetupFolder(string path)
        {
            Folder?.Dispose();
            Folder = new ShellFolder(Environment.ExpandEnvironmentVariables(path), IntPtr.Zero, true);
        }

        private void UnloadFolder()
        {
            Folder?.Dispose();
            Folder = null;
        }

        private void SetItemsSource()
        {
            if (Folder != null)
            {
                ToolbarItems.ItemsSource = Folder.Files;
                ListCollectionView cvs = (ListCollectionView)CollectionViewSource.GetDefaultView(Folder.Files);
                cvs.CustomSort = new ToolbarSorter(this);
            }
        }

        /// <summary>
        /// Moves one icon to a new place (insertIndex is a position in the icons as currently shown,
        /// counting the dragged icon itself) and saves the resulting order. The new order is worked out
        /// and stored here, rather than left to the drag-drop library's own move of the underlying list
        /// followed by the view's re-sort: when it moved an item within the list the sorted view
        /// sometimes never repositioned it, so the drop silently did nothing - the "sometimes works,
        /// sometimes doesn't" reordering.
        /// </summary>
        public void MoveItem(ShellFile dragged, int insertIndex)
        {
            if (Folder == null)
            {
                return;
            }

            ListCollectionView view = (ListCollectionView)CollectionViewSource.GetDefaultView(Folder.Files);
            List<ShellFile> ordered = view.OfType<ShellFile>().ToList();

            int sourceIndex = ordered.IndexOf(dragged);
            if (sourceIndex < 0)
            {
                return;
            }

            ordered.RemoveAt(sourceIndex);
            if (sourceIndex < insertIndex)
            {
                insertIndex--;
            }

            insertIndex = Math.Max(0, Math.Min(insertIndex, ordered.Count));
            ordered.Insert(insertIndex, dragged);

            // This toolbar re-sorts itself below; other monitors' toolbars refresh from the setting.
            _ignoreNextUpdate = true;
            Settings.Instance.QuickLaunchOrder = ordered.Select(file => file.Path).ToList();

            Dictionary<object, Point> before = ItemSlideAnimation.Capture(ToolbarItems);
            view.Refresh();
            ItemSlideAnimation.Play(ToolbarItems, before);
        }

        public void SaveItemOrder()
        {
            List<string> itemPaths = new List<string>();

            foreach (ShellFile file in ((ListCollectionView)CollectionViewSource.GetDefaultView(Folder.Files)).OfType<ShellFile>())
            {
                itemPaths.Add(file.Path);
            }

            // small optimization, only other toolbars with this folder need to reload when the setting is saved.
            _ignoreNextUpdate = true;

            Settings.Instance.QuickLaunchOrder = itemPaths;
        }

        public void AddToSource(StringCollection filesToAdd)
        {
            string sourcePath = Environment.ExpandEnvironmentVariables(Path);

            foreach (string itemPath in filesToAdd)
            {
                // Create shortcut to each dragged file
                try
                {
                    string destinationFileName = System.IO.Path.GetFileNameWithoutExtension(itemPath);
                    string destinationPath = System.IO.Path.Combine(sourcePath, destinationFileName + ".lnk");
                    int dupCount = 0;

                    while (ShellHelper.Exists(destinationPath))
                    {
                        dupCount++;

                        destinationPath = System.IO.Path.Combine(sourcePath, $"{destinationFileName} ({dupCount}).lnk");
                    }

                    ShellLinkHelper.CreateAndSave(itemPath, destinationPath);
                }
                catch (Exception e)
                {
                    ShellLogger.Error($"Toolbar: Unable to save shortcut to {itemPath}", e);
                }
            }
        }

        #region Events
        private static void OnPathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is Toolbar toolbar)
            {
                toolbar.SetupFolder((string)e.NewValue);
            }
        }

        private void ToolbarIcon_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ToolbarButton icon = sender as ToolbarButton;
            if (icon == null)
            {
                return;
            }

            Mouse.Capture(null);
            ShellFile file = icon.DataContext as ShellFile;

            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                return;
            }

            if (InvokeContextMenu(file, false))
            {
                e.Handled = true;
            }
        }

        private void ToolbarIcon_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            ToolbarButton icon = sender as ToolbarButton;
            if (icon == null)
            {
                return;
            }
            
            ShellFile file = icon.DataContext as ShellFile;

            if (InvokeContextMenu(file, true))
            {
                e.Handled = true;
            }
        }

        private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible)
            {
                if (visible)
                {
                    if (Folder != null)
                    {
                        return;
                    }

                    SetupFolder(Path);
                }
                else
                {
                    UnloadFolder();
                }
            }
        }

        private void Toolbar_TaskbarHotkeyPressed(object sender, HotkeyManager.TaskbarHotkeyEventArgs e)
        {
            if (Settings.Instance.WinNumHotkeysAction == WinNumHotkeysOption.InvokeQuickLaunch && Host.Screen.Primary)
            {
                try
                {
                    ListCollectionView items = (ListCollectionView)CollectionViewSource.GetDefaultView(Folder.Files);

                    bool exists = items.MoveCurrentToPosition(e.index);
                    
                    if (exists) InvokeContextMenu((ShellFile)items.CurrentItem, false);

                }
                catch (ArgumentOutOfRangeException) { }
            }
        }
        #endregion

        #region Context menu
        private ShellMenuCommandBuilder GetFileCommandBuilder(ShellFile file)
        {
            if (file == null)
            {
                return new ShellMenuCommandBuilder();
            }

            ShellMenuCommandBuilder builder = new ShellMenuCommandBuilder();

            builder.AddSeparator();
            builder.AddCommand(new ShellMenuCommand
            {
                Flags = MFT.BYCOMMAND,
                Label = (string)FindResource("open_folder"),
                UID = (uint)MenuItem.OpenParentFolder
            });

            return builder;
        }

        private bool InvokeContextMenu(ShellFile file, bool isInteractive)
        {
            if (file == null)
            {
                return false;
            }

            // The menu of a pin leads with the program's jump list (see JumpLists), as the taskbar's button menu does.
            ShellMenuCommandBuilder jumpList = isInteractive ? GetJumpListBuilder(file) : new ShellMenuCommandBuilder();
            var _ = new ShellItemContextMenu(new ShellItem[] { file }, Folder, IntPtr.Zero, HandleFileAction, isInteractive, false, jumpList, GetFileCommandBuilder(file));
            return true;
        }

        // Ids of the jump list's entries in the menu (above the ones of the native menu and this class's own).
        private const uint JumpListHeaderId = 6000;
        private const uint JumpListItemId = 6100;
        private const int JumpListSectionMax = 6;
        private const int JumpListMax = 16;
        private readonly Dictionary<uint, JumpListItem> _jumpListItems = new();

        private ShellMenuCommandBuilder GetJumpListBuilder(ShellFile file)
        {
            var builder = new ShellMenuCommandBuilder();
            _jumpListItems.Clear();

            List<JumpListItem> items;
            try
            {
                items = JumpLists.ForShortcut(file.Path, 200);
            }
            catch (Exception e)
            {
                ShellLogger.Error($"Toolbar: Unable to read the jump list of {file.Path}", e);
                return builder;
            }

            // The user's pinned entries, the recent documents, what the program put in its own categories, and its tasks, last: each
            // under its heading. The program's links to web pages come after its real tasks (some browsers add a dozen of them).
            var sections = new List<(string Heading, List<JumpListItem> Items)>
            {
                (GetString("jump_list_pinned", "Pinned"), items.Where(i => i.Pinned).ToList()),
                (GetString("jump_list_recent", "Recent"), items.Where(i => !i.Pinned && !i.FromProgram).ToList()),
            };

            foreach (IGrouping<string, JumpListItem> category in items.Where(i => i.FromProgram && !string.IsNullOrEmpty(i.Group)).GroupBy(i => i.Group))
            {
                sections.Add((category.Key, category.ToList()));
            }

            sections.Add((GetString("jump_list_tasks", "Tasks"), items
                .Where(i => i.FromProgram && string.IsNullOrEmpty(i.Group))
                .OrderBy(i => i.Arguments != null && i.Arguments.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList()));

            uint header = JumpListHeaderId;
            uint id = JumpListItemId;
            int total = 0;
            foreach ((string heading, List<JumpListItem> section) in sections)
            {
                if (section.Count == 0 || total >= JumpListMax)
                {
                    continue;
                }

                builder.AddCommand(new ShellMenuCommand { Flags = MFT.GRAYED, Label = MenuLabel(heading), UID = header++ });
                foreach (JumpListItem item in section.Take(Math.Min(JumpListSectionMax, JumpListMax - total)))
                {
                    _jumpListItems[id] = item;
                    builder.AddCommand(new ShellMenuCommand { Flags = MFT.BYCOMMAND, Label = MenuLabel(item.Title), UID = id++ });
                    total++;
                }
            }

            if (total > 0)
            {
                builder.AddSeparator();
            }

            return builder;
        }

        private string GetString(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        // A menu label: the ampersand is the mnemonic's marker, and a long name is cut.
        private static string MenuLabel(string text)
        {
            text = (text ?? "").Replace("&", "&&");
            return text.Length > 60 ? text.Substring(0, 59) + "\u2026" : text;
        }

        private bool HandleFileAction(string action, ShellItem[] items, bool allFolders)
        {
            if (action == ((uint)MenuItem.OpenParentFolder).ToString())
            {
                ShellHelper.StartProcess(Folder.Path);
                return true;
            }

            if (uint.TryParse(action, out uint id) && _jumpListItems.TryGetValue(id, out JumpListItem item))
            {
                try
                {
                    var start = new System.Diagnostics.ProcessStartInfo(item.Target) { UseShellExecute = true };
                    if (!string.IsNullOrEmpty(item.Arguments))
                    {
                        start.Arguments = item.Arguments;
                    }

                    if (!string.IsNullOrEmpty(item.WorkingDirectory) && System.IO.Directory.Exists(item.WorkingDirectory))
                    {
                        start.WorkingDirectory = item.WorkingDirectory;
                    }

                    System.Diagnostics.Process.Start(start);
                }
                catch (Exception e)
                {
                    ShellLogger.Error($"Toolbar: Unable to start {item.Target} from a jump list", e);
                }

                return true;
            }

            return false;
        }
        #endregion

        private void Initialize()
        {
            if (!_isLoaded && Host != null)
            {
                Settings.Instance.PropertyChanged += Settings_PropertyChanged;
                Host.hotkeyManager.TaskbarHotkeyPressed += Toolbar_TaskbarHotkeyPressed;

                _isLoaded = true;
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Initialize();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            if (Host != null)
            {
                Host.hotkeyManager.TaskbarHotkeyPressed -= Toolbar_TaskbarHotkeyPressed;
            }

            _isLoaded = false;
        }

        private static void HostChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is Toolbar toolbar && e.OldValue == null && e.NewValue != null)
            {
                toolbar.Initialize();
            }
        }
    }
}