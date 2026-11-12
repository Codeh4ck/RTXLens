using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace RTXLens.Installer.Actions.Configuration;

public static class AfterburnerBackupCleanup
{
    private const string BackupFolder = "AfterburnerBackups";
    private const string LegacyFolder = "RTXLens.AfterburnerBackups";
    private const FileAttributes UnsafeEntry = FileAttributes.Directory | FileAttributes.ReparsePoint;

    private static readonly Regex InstallationKey = new(@"\A[0-9a-f]{20}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ArchiveName = new(@"\A(?:[0-9]{8}-[0-9]{6}-[0-9]{3}-|\.import-)[0-9a-f]{32}\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ImportMarker = new(@"\Alegacy-import-[0-9a-f]{20}\.txt\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OperationResult = new(@"\A[0-9a-f]{32}\.json\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private enum Folder { Legacy, Store, Archive, Results }

    internal static bool IsKnownBackupDirectory(string path) =>
        IsPlainDirectory(path) && IsKnownTree(path, Folder.Store);

    public static void Remove(string? installation, string programData, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(installation))
        {
            TryClean(Path.Combine(installation!, "Desktop", BackupFolder), Folder.Store);
        }

        TryClean(Path.Combine(programData, LegacyFolder), Folder.Legacy);

        void TryClean(string path, Folder kind)
        {
            try
            {
                CleanAt(path, kind);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                log("Could not remove RTX Lens's Afterburner backups: " + error.Message);
            }
        }
    }

    private static bool IsKnownTree(string path, Folder kind)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & UnsafeEntry) == 0 && IsKnownFile(entry, kind))
            {
                continue;
            }

            if ((attributes & UnsafeEntry) != FileAttributes.Directory || ChildKind(entry, kind) is not { } child
                || !IsKnownTree(entry, child))
            {
                return false;
            }
        }

        return true;
    }

    private static void CleanAt(string path, Folder kind)
    {
        path = Path.GetFullPath(path);
        var drive = Path.GetPathRoot(path)!;
        if (drive.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(drive).DriveType != DriveType.Fixed)
        {
            throw new IOException("Afterburner backup cleanup requires a fixed local drive.");
        }

        // Keep each parent open without write/delete sharing so it cannot be replaced by a junction during cleanup.
        var parents = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent != drive; parent = Path.GetDirectoryName(parent))
        {
            parents.Push(parent);
        }

        var handles = new Stack<SafeFileHandle>();
        try
        {
            foreach (var parent in parents)
            {
                var handle = OpenPlainDirectory(parent);
                if (handle is null)
                {
                    return;
                }

                handles.Push(handle);
            }

            CleanDirectory(path, kind);
        }
        finally
        {
            while (handles.Count > 0)
            {
                handles.Pop().Dispose();
            }
        }
    }

    private static void CleanDirectory(string path, Folder kind)
    {
        using (var handle = OpenPlainDirectory(path))
        {
            if (handle is null)
            {
                return;
            }

            foreach (var entry in Directory.GetFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & UnsafeEntry) == 0 && IsKnownFile(entry, kind))
                {
                    File.Delete(entry);
                }
                else if ((attributes & UnsafeEntry) == FileAttributes.Directory && ChildKind(entry, kind) is { } child)
                {
                    CleanDirectory(entry, child);
                }
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return;
            }
        }

        // This is deliberately nonrecursive. The parent is still pinned when this handle is released.
        Directory.Delete(path, recursive: false);
    }

    private static Folder? ChildKind(string path, Folder kind) => kind switch
    {
        Folder.Legacy when InstallationKey.IsMatch(Path.GetFileName(path)) => Folder.Store,
        Folder.Store when ArchiveName.IsMatch(Path.GetFileName(path)) => Folder.Archive,
        Folder.Store when NameIs(path, "OperationResults") => Folder.Results,
        _ => null
    };

    private static bool IsKnownFile(string path, Folder kind) => kind switch
    {
        Folder.Store => NameIs(path, "pending.json") || NameIs(path, ".operation.lock") || NameIs(path, "restart-required.txt")
            || ImportMarker.IsMatch(Path.GetFileName(path)),
        Folder.Archive => NameIs(path, "PSU.cfg") || NameIs(path, "MSIAfterburner.cfg") || NameIs(path, "manifest.json"),
        Folder.Results => OperationResult.IsMatch(Path.GetFileName(path)),
        _ => false
    };

    private static bool NameIs(string path, string name) => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainDirectory(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & UnsafeEntry) == FileAttributes.Directory;

    private static SafeFileHandle? OpenPlainDirectory(string path)
    {
        const uint readAttributes = 0x80;
        const uint openExisting = 3;
        const uint openDirectoryOrReparsePoint = 0x02000000 | 0x00200000;
        var handle = CreateFile(path, readAttributes, FileShare.Read, IntPtr.Zero, openExisting, openDirectoryOrReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3)
            {
                return null;
            }

            throw new Win32Exception(error, "Could not open backup folder: " + path);
        }

        if (GetFileInformationByHandleEx(handle, 9, out var information, 8))
        {
            if ((information.Attributes & UnsafeEntry) == FileAttributes.Directory)
            {
                return handle;
            }

            handle.Dispose();
            return null;
        }

        var detailsError = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(detailsError, "Could not inspect backup folder: " + path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInfo information, uint size);
}
