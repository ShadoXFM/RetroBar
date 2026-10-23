using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RetroBar.Utilities
{
    /// <summary>One entry of a program's jump list: a recent document, or a task or recent item the program put there itself.</summary>
    public sealed class JumpListItem
    {
        public string Title { get; init; }
        public string Target { get; init; }
        public string Arguments { get; init; }
        public string WorkingDirectory { get; init; }

        /// <summary>Put there by the program (its tasks and its own categories), not one of the recent documents Windows keeps.</summary>
        public bool FromProgram { get; init; }

        /// <summary>The name of the category the program put it in (null for its tasks, and for the recent documents).</summary>
        public string Group { get; init; }

        /// <summary>The place among the items the user pinned in the list (0 and up), or -1.</summary>
        public int PinIndex { get; init; } = -1;

        public bool Pinned => PinIndex >= 0;
        public DateTime Time { get; init; }
    }

    /// <summary>
    /// What Windows keeps as the jump list of a program (the recent documents, and what the program itself added): read from the files
    /// in the user's Recent folder, as the taskbar's button menu shows it. A program's list is in files named by a hash of its
    /// application ID (the explicit one, or the one Windows works out from the path of its executable).
    /// </summary>
    public static class JumpLists
    {
        private static string RecentFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent");

        // The reflected form of the polynomial the shell uses (a CRC-64 started at all ones), over the upper-cased ID as UTF-16.
        private const ulong Polynomial = 0x92c64265d32139a4;
        private static readonly ulong[] Table = BuildTable();

        private static ulong[] BuildTable()
        {
            var table = new ulong[256];
            for (int i = 0; i < 256; i++)
            {
                ulong value = (ulong)i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }

        /// <summary>The name (without extension) of the jump list files of an application ID.</summary>
        public static string AppIdHash(string appId)
        {
            ulong crc = ulong.MaxValue;
            foreach (byte b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
            {
                crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
            }

            return crc.ToString("x");
        }

        // The folders whose place in a path Windows writes as their ID when it works out an application ID from an executable's path.
        private static readonly (string Id, Environment.SpecialFolder Folder)[] KnownFolders =
        {
            ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.SpecialFolder.ProgramFilesX86),
            ("{905E63B6-C1BF-494E-B29C-65B732D3D21A}", Environment.SpecialFolder.ProgramFiles),
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.SpecialFolder.System),
            ("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", Environment.SpecialFolder.SystemX86),
            ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.SpecialFolder.Windows),
            ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", Environment.SpecialFolder.ApplicationData),
            ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", Environment.SpecialFolder.LocalApplicationData),
            ("{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}", Environment.SpecialFolder.CommonApplicationData),
        };

        /// <summary>The application IDs a pinned program can have, most likely first: its own (as the shortcut says), then the ones Windows works out.</summary>
        public static List<string> AppIdCandidates(string shortcutPath)
        {
            var ids = new List<string>();
            ShortcutInfo link = ShortcutFile.Read(shortcutPath);
            if (link == null)
            {
                return ids;
            }

            if (!string.IsNullOrEmpty(link.AppUserModelId))
            {
                ids.Add(link.AppUserModelId);
            }

            string exe = link.Target;
            if (string.IsNullOrEmpty(exe) || exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add("Microsoft.Windows.Explorer");
            }

            if (!string.IsNullOrEmpty(exe))
            {
                // An updater that starts the program ("Update.exe --processStart Discord.exe"): Squirrel's IDs are com.squirrel.<folder>.<program>.
                const string processStart = "--processStart ";
                int at = link.Arguments?.IndexOf(processStart, StringComparison.OrdinalIgnoreCase) ?? -1;
                if (at >= 0)
                {
                    string started = link.Arguments.Substring(at + processStart.Length).Trim().Trim('"').Split(' ')[0];
                    string folder = Path.GetFileName(Path.GetDirectoryName(exe));
                    if (!string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(started))
                    {
                        ids.Add($"com.squirrel.{folder}.{Path.GetFileNameWithoutExtension(started)}");
                    }
                }

                ids.Add(exe);
                foreach ((string id, Environment.SpecialFolder folder) in KnownFolders)
                {
                    string root = Environment.GetFolderPath(folder);
                    if (!string.IsNullOrEmpty(root) && exe.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        ids.Add(id + exe.Substring(root.Length));
                    }
                }
            }

            return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The jump list of the pinned program a shortcut starts: up to <paramref name="max"/> entries, the program's own first.</summary>
        public static List<JumpListItem> ForShortcut(string shortcutPath, int max)
        {
            var own = new List<JumpListItem>();
            var recent = new List<JumpListItem>();

            try
            {
                foreach (string id in AppIdCandidates(shortcutPath))
                {
                    string hash = AppIdHash(id);

                    string custom = Path.Combine(RecentFolder, "CustomDestinations", hash + ".customDestinations-ms");
                    if (File.Exists(custom))
                    {
                        own.AddRange(ReadCustomDestinations(custom));
                    }

                    string automatic = Path.Combine(RecentFolder, "AutomaticDestinations", hash + ".automaticDestinations-ms");
                    if (File.Exists(automatic))
                    {
                        recent.AddRange(ReadAutomaticDestinations(automatic));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                // A list that cannot be read is no list.
            }

            return own
                .Concat(recent.OrderBy(item => item.Pinned ? item.PinIndex : int.MaxValue).ThenByDescending(item => item.Time))
                .Where(item => !string.IsNullOrEmpty(item.Target))
                .GroupBy(item => (item.Title, item.Target, item.Arguments))
                .Select(group => group.First())
                .Take(max)
                .ToList();
        }

        /// <summary>What the program put in its own jump list (the tasks and its categories): a series of shortcuts.</summary>
        public static List<JumpListItem> ReadCustomDestinations(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var items = new List<JumpListItem>();

            // Every shortcut starts with the same header (its size, 0x4C, and the shell link's class ID); a category's footer, AB FB BF BA,
            // ends the run of them.
            byte[] signature = { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 };
            for (int at = IndexOf(data, signature, 0); at >= 0; at = IndexOf(data, signature, at + signature.Length))
            {
                ShortcutInfo link = ShortcutFile.Parse(data, at);
                if (link != null && !(string.IsNullOrEmpty(link.Target) && string.IsNullOrEmpty(link.Title)))
                {
                    items.Add(new JumpListItem
                    {
                        Title = !string.IsNullOrEmpty(link.Title) ? link.Title : Path.GetFileName(link.Target),
                        Target = link.Target,
                        Arguments = link.Arguments,
                        WorkingDirectory = link.WorkingDirectory,
                        FromProgram = true,
                        Group = link.Group,
                    });
                }
            }

            return items;
        }

        /// <summary>The recent documents Windows keeps for a program: a compound file with one shortcut per document and a list that says in what order.</summary>
        public static List<JumpListItem> ReadAutomaticDestinations(string path)
        {
            var items = new List<JumpListItem>();
            CompoundFile file = CompoundFile.Open(path);
            if (file == null)
            {
                return items;
            }

            Dictionary<string, byte[]> streams = file.Streams();
            var order = new Dictionary<uint, (DateTime Time, int Pin)>();
            if (streams.TryGetValue("DestList", out byte[] list))
            {
                ParseDestList(list, order);
            }

            foreach ((string name, byte[] data) in streams)
            {
                if (name == "DestList" || !uint.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out uint id))
                {
                    continue;
                }

                ShortcutInfo link = ShortcutFile.Parse(data, 0);
                if (link == null || string.IsNullOrEmpty(link.Target))
                {
                    continue;
                }

                order.TryGetValue(id, out var known);
                bool isPath = link.Target.Length > 2 && (link.Target[1] == ':' || link.Target.StartsWith(@"\\"));
                if (isPath && !File.Exists(link.Target) && !Directory.Exists(link.Target))
                {
                    continue;
                }

                items.Add(new JumpListItem
                {
                    Title = !string.IsNullOrEmpty(link.Title) ? link.Title : (Path.GetFileName(link.Target.TrimEnd('\\')) is { Length: > 0 } name2 ? name2 : link.Target),
                    Target = link.Target,
                    Arguments = link.Arguments,
                    WorkingDirectory = link.WorkingDirectory,
                    PinIndex = order.ContainsKey(id) ? known.Pin : -1,
                    Time = known.Time,
                });
            }

            return items;
        }

        // The list's entries (Windows 10 and 11 layout, versions 3 and up): after a 32-byte header, each has 130 bytes of fixed fields and
        // then its path (UTF-16, its length in characters at 128), and 4 more bytes. The fields used here: the number of the entry's
        // stream at 88, when it was last used (a FILETIME, none for a pinned one) at 100 and its pin position at 108 (-1 when it is
        // not pinned).
        private static void ParseDestList(byte[] list, Dictionary<uint, (DateTime Time, int Pin)> order)
        {
            if (list.Length < 32)
            {
                return;
            }

            uint version = BitConverter.ToUInt32(list, 0);
            if (version < 3)
            {
                return;
            }

            int at = 32;
            while (at + 130 <= list.Length)
            {
                uint id = BitConverter.ToUInt32(list, at + 88);
                long ticks = BitConverter.ToInt64(list, at + 100);
                int pin = BitConverter.ToInt32(list, at + 108);
                int chars = BitConverter.ToUInt16(list, at + 128);

                DateTime time = DateTime.MinValue;
                if (ticks > 0 && ticks < 2650467743999999999)
                {
                    time = DateTime.FromFileTimeUtc(ticks);
                }

                order[id] = (time, pin);
                at += 130 + chars * 2 + (version >= 3 ? 4 : 0);
            }
        }

        private static int IndexOf(byte[] data, byte[] pattern, int from)
        {
            for (int i = Math.Max(0, from); i <= data.Length - pattern.Length; i++)
            {
                if (data[i] != pattern[0])
                {
                    continue;
                }

                int j = 1;
                while (j < pattern.Length && data[i + j] == pattern[j])
                {
                    j++;
                }

                if (j == pattern.Length)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>What is read from a shortcut (.lnk) file.</summary>
    public sealed class ShortcutInfo
    {
        public string Target { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }

        /// <summary>The title the property store gives it (what a program names its jump list task).</summary>
        public string Title { get; set; }

        /// <summary>The program's own application ID, as the property store has it.</summary>
        public string AppUserModelId { get; set; }

        /// <summary>The name of the category of the program's jump list it belongs to.</summary>
        public string Group { get; set; }
    }

    /// <summary>A reader of the shortcut format (the shell link file, MS-SHLLINK), as far as the target, the arguments and two properties.</summary>
    public static class ShortcutFile
    {
        public static ShortcutInfo Read(string path)
        {
            try
            {
                return Parse(File.ReadAllBytes(path), 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static readonly Guid SummaryInformation = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
        private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private static readonly Guid LinkArguments = new("436F2667-14E2-4FEB-B30A-146C53B5B674");

        public static ShortcutInfo Parse(byte[] d, int start)
        {
            try
            {
                if (start < 0 || start + 76 > d.Length || BitConverter.ToUInt32(d, start) != 0x4C)
                {
                    return null;
                }

                uint flags = BitConverter.ToUInt32(d, start + 0x14);
                bool hasIdList = (flags & 0x01) != 0;
                bool hasLinkInfo = (flags & 0x02) != 0;
                bool unicode = (flags & 0x80) != 0;
                int at = start + 0x4C;
                var info = new ShortcutInfo();

                if (hasIdList)
                {
                    at += 2 + BitConverter.ToUInt16(d, at);
                }

                if (hasLinkInfo)
                {
                    int linkInfo = at;
                    int size = (int)BitConverter.ToUInt32(d, linkInfo);
                    int headerSize = (int)BitConverter.ToUInt32(d, linkInfo + 4);
                    uint infoFlags = BitConverter.ToUInt32(d, linkInfo + 8);
                    if ((infoFlags & 1) != 0)
                    {
                        string basePath;
                        string suffix;
                        if (headerSize >= 0x24)
                        {
                            basePath = ReadUtf16Z(d, linkInfo + (int)BitConverter.ToUInt32(d, linkInfo + 28));
                            suffix = ReadUtf16Z(d, linkInfo + (int)BitConverter.ToUInt32(d, linkInfo + 32));
                        }
                        else
                        {
                            basePath = ReadAnsiZ(d, linkInfo + (int)BitConverter.ToUInt32(d, linkInfo + 16));
                            suffix = ReadAnsiZ(d, linkInfo + (int)BitConverter.ToUInt32(d, linkInfo + 24));
                        }

                        info.Target = string.IsNullOrEmpty(suffix) ? basePath : Path.Combine(basePath, suffix);
                    }

                    at += size;
                }

                string ReadString()
                {
                    int count = BitConverter.ToUInt16(d, at);
                    at += 2;
                    string text = unicode ? Encoding.Unicode.GetString(d, at, count * 2) : Encoding.Default.GetString(d, at, count);
                    at += unicode ? count * 2 : count;
                    return text;
                }

                if ((flags & 0x04) != 0) { ReadString(); }                                  // the description
                string relative = (flags & 0x08) != 0 ? ReadString() : null;
                if ((flags & 0x10) != 0) { info.WorkingDirectory = ReadString(); }
                if ((flags & 0x20) != 0) { info.Arguments = ReadString(); }
                if ((flags & 0x40) != 0) { ReadString(); }                                  // the icon

                if (string.IsNullOrEmpty(info.Target) && !string.IsNullOrEmpty(relative) && !string.IsNullOrEmpty(info.WorkingDirectory))
                {
                    info.Target = Path.GetFullPath(Path.Combine(info.WorkingDirectory, relative));
                }

                // The extra data blocks: a size, a signature, and the block; a size under 4 ends them. The property store is 0xA0000009.
                while (at + 8 <= d.Length)
                {
                    int blockSize = (int)BitConverter.ToUInt32(d, at);
                    if (blockSize < 4)
                    {
                        break;
                    }

                    if (BitConverter.ToUInt32(d, at + 4) == 0xA0000009)
                    {
                        ReadPropertyStore(d, at + 8, at + blockSize, info);
                    }

                    at += blockSize;
                }

                return info;
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                return null;
            }
        }

        // Serialized property storages: a size, "1SPS", the format's ID, then the properties (a size, an ID, a reserved byte, and a value
        // that starts with its type), the storage ending at a property of size 0.
        private static void ReadPropertyStore(byte[] d, int at, int end, ShortcutInfo info)
        {
            while (at + 24 <= end)
            {
                int storageSize = (int)BitConverter.ToUInt32(d, at);
                if (storageSize < 24 || at + storageSize > end + 4)
                {
                    return;
                }

                var format = new Guid(d.AsSpan(at + 8, 16));
                int property = at + 24;
                while (property + 9 <= at + storageSize)
                {
                    int size = (int)BitConverter.ToUInt32(d, property);
                    if (size == 0)
                    {
                        break;
                    }

                    uint id = BitConverter.ToUInt32(d, property + 4);
                    ushort type = BitConverter.ToUInt16(d, property + 9);
                    if (type == 0x1F && property + 17 <= d.Length)
                    {
                        int chars = (int)BitConverter.ToUInt32(d, property + 13);
                        if (chars > 0 && chars < 4096 && property + 17 + chars * 2 <= d.Length)
                        {
                            string text = Encoding.Unicode.GetString(d, property + 17, chars * 2).TrimEnd('\0');
                            if (format == AppUserModel && id == 27 && !string.IsNullOrEmpty(text))
                            {
                                // What the program called the item in its list (the jump list's own title).
                                info.Title = text;
                            }
                            else if (format == AppUserModel && id == 30)
                            {
                                info.Group = text;
                            }
                            else if (format == AppUserModel && id == 5)
                            {
                                info.AppUserModelId = text;
                            }
                            else if (format == SummaryInformation && id == 2 && string.IsNullOrEmpty(info.Title))
                            {
                                info.Title = text;
                            }
                            else if (format == LinkArguments && id == 100 && string.IsNullOrEmpty(info.Arguments))
                            {
                                info.Arguments = text;
                            }
                        }
                    }

                    property += size;
                }

                at += storageSize;
            }
        }

        private static string ReadUtf16Z(byte[] d, int at)
        {
            int end = at;
            while (end + 1 < d.Length && (d[end] != 0 || d[end + 1] != 0))
            {
                end += 2;
            }

            return Encoding.Unicode.GetString(d, at, end - at);
        }

        private static string ReadAnsiZ(byte[] d, int at)
        {
            int end = at;
            while (end < d.Length && d[end] != 0)
            {
                end++;
            }

            return Encoding.Default.GetString(d, at, end - at);
        }
    }

    /// <summary>A reader of the compound file format (OLE structured storage): the streams of its root, by name.</summary>
    internal sealed class CompoundFile
    {
        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint FreeSector = 0xFFFFFFFF;

        private readonly byte[] _data;
        private int _sectorSize;
        private int _miniSectorSize;
        private uint _miniCutoff;
        private List<uint> _fat;
        private List<uint> _miniFat;
        private byte[] _miniStream;

        private CompoundFile(byte[] data)
        {
            _data = data;
        }

        public static CompoundFile Open(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 512 || BitConverter.ToUInt64(data, 0) != 0xE11AB1A1E011CFD0)
            {
                return null;
            }

            return new CompoundFile(data);
        }

        private int SectorOffset(uint sector) => (int)((sector + 1) * (long)_sectorSize);

        private uint[] ReadSector(uint sector)
        {
            int offset = SectorOffset(sector);
            if (offset < 0 || offset + _sectorSize > _data.Length)
            {
                throw new InvalidDataException("A sector is outside the file.");
            }

            var values = new uint[_sectorSize / 4];
            Buffer.BlockCopy(_data, offset, values, 0, _sectorSize);
            return values;
        }

        private byte[] ReadChain(uint start, long size, bool mini)
        {
            var result = new List<byte>();
            int unit = mini ? _miniSectorSize : _sectorSize;
            List<uint> chain = mini ? _miniFat : _fat;
            uint sector = start;
            int guard = 0;

            while (sector < 0xFFFFFFF0 && guard++ < 1_000_000 && (size < 0 || result.Count < size))
            {
                int offset;
                byte[] source;
                if (mini)
                {
                    source = _miniStream;
                    offset = (int)(sector * (long)unit);
                }
                else
                {
                    source = _data;
                    offset = SectorOffset(sector);
                }

                if (offset < 0 || offset + unit > source.Length)
                {
                    break;
                }

                result.AddRange(new ArraySegment<byte>(source, offset, unit));
                if (sector >= chain.Count)
                {
                    break;
                }

                sector = chain[(int)sector];
            }

            if (size >= 0 && result.Count > size)
            {
                result.RemoveRange((int)size, result.Count - (int)size);
            }

            return result.ToArray();
        }

        public Dictionary<string, byte[]> Streams()
        {
            _sectorSize = 1 << BitConverter.ToUInt16(_data, 0x1E);
            _miniSectorSize = 1 << BitConverter.ToUInt16(_data, 0x20);
            uint firstDirectory = BitConverter.ToUInt32(_data, 0x30);
            _miniCutoff = BitConverter.ToUInt32(_data, 0x38);
            uint firstMiniFat = BitConverter.ToUInt32(_data, 0x3C);
            uint firstDifat = BitConverter.ToUInt32(_data, 0x44);
            uint difatCount = BitConverter.ToUInt32(_data, 0x48);
            uint fatCount = BitConverter.ToUInt32(_data, 0x2C);

            // The sectors of the allocation table: 109 in the header, then the ones the extra table sectors list.
            var fatSectors = new List<uint>();
            for (int i = 0; i < 109 && fatSectors.Count < fatCount; i++)
            {
                uint sector = BitConverter.ToUInt32(_data, 0x4C + i * 4);
                if (sector < 0xFFFFFFF0)
                {
                    fatSectors.Add(sector);
                }
            }

            uint difat = firstDifat;
            for (uint n = 0; n < difatCount && difat < 0xFFFFFFF0; n++)
            {
                uint[] entries = ReadSector(difat);
                for (int i = 0; i < entries.Length - 1 && fatSectors.Count < fatCount; i++)
                {
                    if (entries[i] < 0xFFFFFFF0)
                    {
                        fatSectors.Add(entries[i]);
                    }
                }

                difat = entries[^1];
            }

            _fat = new List<uint>();
            foreach (uint sector in fatSectors)
            {
                _fat.AddRange(ReadSector(sector));
            }

            byte[] directory = ReadChain(firstDirectory, -1, false);
            int entryCount = directory.Length / 128;

            // The root entry's stream is the mini stream that holds the small streams.
            uint rootStart = BitConverter.ToUInt32(directory, 0x74);
            long rootSize = BitConverter.ToUInt32(directory, 0x78);
            _miniStream = ReadChain(rootStart, rootSize, false);

            _miniFat = new List<uint>();
            if (firstMiniFat < 0xFFFFFFF0)
            {
                byte[] table = ReadChain(firstMiniFat, -1, false);
                for (int i = 0; i + 4 <= table.Length; i += 4)
                {
                    _miniFat.Add(BitConverter.ToUInt32(table, i));
                }
            }

            var streams = new Dictionary<string, byte[]>();
            for (int i = 1; i < entryCount; i++)
            {
                int at = i * 128;
                byte type = directory[at + 0x42];
                int nameBytes = BitConverter.ToUInt16(directory, at + 0x40);
                if (type != 2 || nameBytes < 2)
                {
                    continue;
                }

                string name = Encoding.Unicode.GetString(directory, at, nameBytes - 2);
                uint start = BitConverter.ToUInt32(directory, at + 0x74);
                long size = BitConverter.ToUInt32(directory, at + 0x78);
                streams[name] = ReadChain(start, size, size < _miniCutoff);
            }

            return streams;
        }
    }
}
