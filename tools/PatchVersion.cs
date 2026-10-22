// Replaces the neutral Win32 VERSIONINFO resource of a built Gestosh executable
// with one that carries a description for every supported UI language.
//
// The in-box C# compiler can only emit a single, language-neutral version block
// (FileDescription comes from AssemblyTitle). Explorer, Task Manager and the
// "Details" property page read the Win32 resource, so a multi-language summary
// has to be written as a VERSIONINFO structure with one StringTable per language.
// csc refuses /win32res together with /win32icon and /win32manifest, so the
// resource is patched in place after the build instead of being compiled in.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

static class PatchVersion
{
    const ushort Neutral = 0;
    const ushort UnicodeCodePage = 0x04B0;
    const ushort RtVersion = 16;
    const ushort VersionResourceId = 1;

    sealed class Translation
    {
        public readonly ushort Language;
        public readonly string FileDescription;
        public readonly string Comments;

        public Translation(ushort language, string fileDescription, string comments)
        {
            Language = language;
            FileDescription = fileDescription;
            Comments = comments;
        }
    }

    // Order matters: Windows walks the tables and picks the first exact match for
    // the user's UI language, then falls back to the first table in the list.
    static readonly Translation[] Translations =
    {
        new Translation(0x0409,
            "Gestosh - precision touchpad gestures",
            "Three-finger drag, four-finger App Expose, Task View and Action Center gestures, five-finger edge desktop. No third-party libraries."),
        new Translation(0x0804,
            "Gestosh - 精密触摸板手势工具",
            "三指拖拽、四指 App Expose、任务视图与操作中心手势、五指贴边显示桌面；不依赖任何第三方库。"),
        new Translation(0x040C,
            "Gestosh - gestes du pavé tactile de précision",
            "Glisser à trois doigts, App Expose à quatre doigts, gestes Vue des tâches et Centre d’actions, bureau en bordure à cinq doigts. Sans bibliothèque tierce."),
        new Translation(0x0419,
            "Gestosh - жесты точной сенсорной панели",
            "Перетаскивание тремя пальцами, App Expose четырьмя пальцами, жесты представления задач и центра уведомлений, рабочий стол у края пятью пальцами. Без сторонних библиотек."),
        new Translation(0x0C0A,
            "Gestosh - gestos del panel táctil de precisión",
            "Arrastre con tres dedos, App Expose con cuatro dedos, gestos de vista de tareas y centro de actividades, escritorio en el borde con cinco dedos. Sin bibliotecas de terceros."),
        new Translation(0x0407,
            "Gestosh - Gesten für Präzisionstouchpads",
            "Ziehen mit drei Fingern, App Expose mit vier Fingern, Taskansicht- und Info-Center-Gesten, Desktop am Rand mit fünf Fingern. Ohne Fremdbibliotheken."),
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EndUpdateResource(IntPtr update, bool discard);

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: PatchVersion <exe> [file-version]");
            return 2;
        }

        string path = Path.GetFullPath(args[0]);
        string version = args.Length > 1 ? args[1] : "1.5.0.0";
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("file not found: " + path);
            return 2;
        }

        byte[] blob = BuildVersionBlob(version);
        IntPtr update = BeginUpdateResource(path, false);
        if (update == IntPtr.Zero)
        {
            Console.Error.WriteLine("BeginUpdateResource failed: " + Marshal.GetLastWin32Error());
            return 3;
        }

        bool ok;
        try
        {
            ok = UpdateResource(update, new IntPtr(RtVersion), new IntPtr(VersionResourceId), Neutral, blob, (uint)blob.Length);
        }
        catch
        {
            EndUpdateResource(update, true);
            throw;
        }

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            EndUpdateResource(update, true);
            Console.Error.WriteLine("UpdateResource failed: " + error);
            return 4;
        }

        if (!EndUpdateResource(update, false))
        {
            Console.Error.WriteLine("EndUpdateResource failed: " + Marshal.GetLastWin32Error());
            return 5;
        }

        Console.WriteLine("version resource updated: " + Path.GetFileName(path) + " " + version + " (" + Translations.Length + " languages)");
        return 0;
    }

    static byte[] BuildVersionBlob(string version)
    {
        int[] parts = ParseVersion(version);

        List<byte> buffer = new List<byte>(4096);
        int root = StartBlock(buffer, 52, 0, "VS_VERSION_INFO");

        // VS_FIXEDFILEINFO, exactly 52 bytes.
        AddU32(buffer, 0xFEEF04BD);                    // dwSignature
        AddU32(buffer, 0x00010000);                    // dwStrucVersion
        AddU32(buffer, ((uint)parts[0] << 16) | (uint)parts[1]);
        AddU32(buffer, ((uint)parts[2] << 16) | (uint)parts[3]);
        AddU32(buffer, ((uint)parts[0] << 16) | (uint)parts[1]);
        AddU32(buffer, ((uint)parts[2] << 16) | (uint)parts[3]);
        AddU32(buffer, 0x0000003F);                    // dwFileFlagsMask
        AddU32(buffer, 0x00000000);                    // dwFileFlags
        AddU32(buffer, 0x00040004);                    // VOS_NT_WINDOWS32
        AddU32(buffer, 0x00000001);                    // VFT_APP
        AddU32(buffer, 0x00000000);                    // dwFileSubtype
        AddU32(buffer, 0x00000000);                    // dwFileDateMS
        AddU32(buffer, 0x00000000);                    // dwFileDateLS
        Pad4(buffer);

        int stringFileInfo = StartBlock(buffer, 0, 1, "StringFileInfo");
        for (int i = 0; i < Translations.Length; i++)
        {
            Translation translation = Translations[i];
            string table = string.Format(CultureInfo.InvariantCulture, "{0:X4}{1:X4}", translation.Language, UnicodeCodePage);
            int tableBlock = StartBlock(buffer, 0, 1, table);

            AddString(buffer, "CompanyName", "EZ118");
            AddString(buffer, "FileDescription", translation.FileDescription);
            AddString(buffer, "FileVersion", version);
            AddString(buffer, "InternalName", "Gestosh");
            AddString(buffer, "LegalCopyright", "Copyright \u00A9 2026 EZ118. All rights reserved.");
            AddString(buffer, "OriginalFilename", "Gestosh.exe");
            AddString(buffer, "ProductName", "Gestosh");
            AddString(buffer, "ProductVersion", ProductVersion(version));
            AddString(buffer, "Comments", translation.Comments);
            AddString(buffer, "Author", "EZ118");

            CloseBlock(buffer, tableBlock);
        }
        CloseBlock(buffer, stringFileInfo);

        int varFileInfo = StartBlock(buffer, 0, 1, "VarFileInfo");
        int translationBlock = StartBlock(buffer, (ushort)(Translations.Length * 4), 0, "Translation");
        for (int i = 0; i < Translations.Length; i++)
        {
            AddU16(buffer, Translations[i].Language);
            AddU16(buffer, UnicodeCodePage);
        }
        CloseBlock(buffer, translationBlock);
        CloseBlock(buffer, varFileInfo);

        CloseBlock(buffer, root);
        return buffer.ToArray();
    }

    static int[] ParseVersion(string version)
    {
        string[] fields = version.Split('.');
        int[] parts = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            int value;
            if (i < fields.Length && int.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                parts[i] = value < 0 ? 0 : (value > 0xFFFF ? 0xFFFF : value);
            }
        }
        return parts;
    }

    static string ProductVersion(string version)
    {
        string[] fields = version.Split('.');
        return fields.Length >= 3 ? fields[0] + "." + fields[1] + "." + fields[2] : version;
    }

    static int StartBlock(List<byte> buffer, ushort valueLength, ushort type, string key)
    {
        Pad4(buffer);
        int start = buffer.Count;
        AddU16(buffer, 0);              // wLength, patched by CloseBlock
        AddU16(buffer, valueLength);    // wValueLength
        AddU16(buffer, type);           // wType
        AddWChars(buffer, key);
        AddU16(buffer, 0);              // NUL terminator
        Pad4(buffer);
        return start;
    }

    static void CloseBlock(List<byte> buffer, int start)
    {
        Pad4(buffer);
        int length = buffer.Count - start;
        if (length > 0xFFFF)
        {
            throw new InvalidOperationException("VERSIONINFO block is too large.");
        }
        buffer[start] = (byte)(length & 0xFF);
        buffer[start + 1] = (byte)((length >> 8) & 0xFF);
    }

    static void AddString(List<byte> buffer, string key, string value)
    {
        int block = StartBlock(buffer, (ushort)(value.Length + 1), 1, key);
        AddWChars(buffer, value);
        AddU16(buffer, 0);
        CloseBlock(buffer, block);
    }

    static void AddWChars(List<byte> buffer, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            AddU16(buffer, text[i]);
        }
    }

    static void AddU16(List<byte> buffer, int value)
    {
        buffer.Add((byte)(value & 0xFF));
        buffer.Add((byte)((value >> 8) & 0xFF));
    }

    static void AddU32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value & 0xFF));
        buffer.Add((byte)((value >> 8) & 0xFF));
        buffer.Add((byte)((value >> 16) & 0xFF));
        buffer.Add((byte)((value >> 24) & 0xFF));
    }

    static void Pad4(List<byte> buffer)
    {
        while ((buffer.Count & 3) != 0)
        {
            buffer.Add(0);
        }
    }
}
