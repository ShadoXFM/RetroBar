using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RetroBar.Controls
{
    // The jump list of the program a tab stands for, at the top of the tab's right-click menu (as the taskbar's button menu has it): what
    // the user pinned in it, the recent documents, what the program put there itself (its categories and tasks), and, for a
    // browser, the pages it has been to. Read from the files Windows keeps in the Recent folder (see JumpLists) and the browser's own
    // History file (see BrowserHistory) each time the menu is opened.
    public partial class TaskButton
    {
        private const int JumpListSectionMax = 6;
        private const int HistorySectionMax = 8;
        private const int JumpListMax = 24;

        // What was put in the menu the last time it opened, to be taken out the next time.
        private readonly List<object> _jumpListMenuItems = new();

        private string JumpListString(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void UpdateJumpListMenu()
        {
            ContextMenu menu = AppButton.ContextMenu;
            if (menu == null)
            {
                return;
            }

            foreach (object old in _jumpListMenuItems)
            {
                menu.Items.Remove(old);
            }

            _jumpListMenuItems.Clear();

            if (Window == null)
            {
                return;
            }

            var sections = new List<(string Heading, List<JumpListItem> Items, int Max)>();
            try
            {
                string exe = Window.IsUWP ? null : Window.WinFileName;
                var ids = new List<string>();
                string explicitId = Window.AppUserModelID;
                if (!string.IsNullOrEmpty(explicitId) && !(exe != null && explicitId.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)))
                {
                    ids.Add(explicitId);
                }

                string pin = Window.IsUWP ? null : PinnedApps.FindLink(Window);
                if (pin != null)
                {
                    ids.AddRange(JumpLists.AppIdCandidates(pin));
                }

                ids.AddRange(JumpLists.AppIdCandidates(exe, null, null));
                List<JumpListItem> items = JumpLists.ForApp(ids, 200);

                sections.Add((JumpListString("jump_list_pinned", "Pinned"), items.Where(i => i.Pinned).ToList(), JumpListSectionMax));
                sections.Add((JumpListString("jump_list_recent", "Recent"), items.Where(i => !i.Pinned && !i.FromProgram).ToList(), JumpListSectionMax));

                foreach (IGrouping<string, JumpListItem> category in items.Where(i => i.FromProgram && !string.IsNullOrEmpty(i.Group)).GroupBy(i => i.Group))
                {
                    sections.Add((category.Key, category.ToList(), JumpListSectionMax));
                }

                // A browser's pages (the pages are opened by the browser that is being asked).
                if (exe != null)
                {
                    List<JumpListItem> pages = BrowserHistory.Recent(exe, HistorySectionMax)
                        .Select(page => new JumpListItem { Title = page.Title, Target = exe, Arguments = "\"" + page.Url + "\"" })
                        .ToList();
                    sections.Add((JumpListString("jump_list_history", "History"), pages, HistorySectionMax));
                }

                // The program's tasks, then its links to web pages (a browser's most visited sites, or some promoted ones) last.
                static bool IsLink(JumpListItem i) => i.Arguments != null && i.Arguments.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase);
                sections.Add((JumpListString("jump_list_tasks", "Tasks"), items.Where(i => i.FromProgram && string.IsNullOrEmpty(i.Group) && !IsLink(i)).ToList(), JumpListSectionMax));
                sections.Add((JumpListString("jump_list_links", "Links"), items.Where(i => i.FromProgram && string.IsNullOrEmpty(i.Group) && IsLink(i)).ToList(), JumpListSectionMax));
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("TaskButton: Unable to read the jump list", ex);
                return;
            }

            int index = 0;
            int total = 0;
            foreach ((string heading, List<JumpListItem> section, int max) in sections)
            {
                if (section.Count == 0 || total >= JumpListMax)
                {
                    continue;
                }

                AddJumpListMenuItem(menu, ref index, new MenuItem { Header = MenuText(heading), IsEnabled = false, FontStyle = FontStyles.Italic, Style = MenuItemStyle.Style });
                foreach (JumpListItem item in section.Take(Math.Min(max, JumpListMax - total)))
                {
                    JumpListItem chosen = item;
                    var entry = new MenuItem { Header = MenuText(item.Title), Style = MenuItemStyle.Style };
                    entry.Click += (s, args) => StartJumpListItem(chosen);
                    AddJumpListMenuItem(menu, ref index, entry);
                    total++;
                }
            }

            if (total > 0)
            {
                AddJumpListMenuItem(menu, ref index, new Separator());
            }
        }

        private void AddJumpListMenuItem(ContextMenu menu, ref int index, object item)
        {
            menu.Items.Insert(index++, item);
            _jumpListMenuItems.Add(item);
        }

        // A menu text: the underscore is the access key's marker, and a long name is cut.
        private static string MenuText(string text)
        {
            text = (text ?? "").Replace("_", "__");
            return text.Length > 60 ? text.Substring(0, 59) + "…" : text;
        }

        private static void StartJumpListItem(JumpListItem item)
        {
            try
            {
                var start = new ProcessStartInfo(item.Target) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(item.Arguments))
                {
                    start.Arguments = item.Arguments;
                }

                if (!string.IsNullOrEmpty(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                {
                    start.WorkingDirectory = item.WorkingDirectory;
                }

                Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ManagedShell.Common.Logging.ShellLogger.Error($"TaskButton: Unable to start {item.Target} from a jump list", ex);
            }
        }
    }
}
