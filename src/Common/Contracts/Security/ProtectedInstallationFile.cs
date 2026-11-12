using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RTXLens.Contracts.Security;

#if !NETFRAMEWORK
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public static class ProtectedInstallationFile
{
    public static void Require(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The installed executable or script could not be found.", path);
        }

        var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        const FileSystemRights writeAccess = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                                             | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var drive = Path.GetPathRoot(path)!;
        if (drive.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(drive).DriveType != DriveType.Fixed)
        {
            throw new UnauthorizedAccessException("An administrator-protected folder on a local drive is required.");
        }

        for (string? current = path; current is not null && current != drive; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The installed executable or script is behind a linked path.");
            }

            var security = File.Exists(current)
                ? (FileSystemSecurity)new FileInfo(current).GetAccessControl()
                : new DirectoryInfo(current).GetAccessControl();
            if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))
                || security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                    .Any(rule => rule.AccessControlType == AccessControlType.Allow
                        && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                        && (rule.FileSystemRights & writeAccess) != 0 && !Trusted(rule.IdentityReference)))
            {
                throw new UnauthorizedAccessException("An administrator-protected installation folder is required: " + current);
            }
        }

        return;

        bool Trusted(IdentityReference? sid) => Equals(sid, administrators) || Equals(sid, system) || Equals(sid, trustedInstaller);
    }
}