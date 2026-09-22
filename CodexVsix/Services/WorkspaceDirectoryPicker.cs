using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodexVsix.Services;

internal static class WorkspaceDirectoryPicker
{
    public static string? Pick(string initialDirectory, string title)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell
            ?? throw new InvalidOperationException("The Visual Studio folder picker is unavailable.");
        ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
        const int capacity = 32768;
        var buffer = Marshal.AllocCoTaskMem(capacity * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var browseInfo = new[]
            {
                new VSBROWSEINFOW
                {
                    lStructSize = (uint)Marshal.SizeOf(typeof(VSBROWSEINFOW)),
                    hwndOwner = owner,
                    pwzDlgTitle = title,
                    pwzDirName = buffer,
                    nMaxDirName = capacity,
                    pwzInitialDir = Directory.Exists(initialDirectory) ? initialDirectory : null
                }
            };
            var result = shell.GetDirectoryViaBrowseDlg(browseInfo);
            if (result == VSConstants.OLE_E_PROMPTSAVECANCELLED || result == VSConstants.S_FALSE)
            {
                return null;
            }

            ErrorHandler.ThrowOnFailure(result);
            var selectedDirectory = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(selectedDirectory) ? null : selectedDirectory;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
