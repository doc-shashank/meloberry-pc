using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Meloberry.Wrapper.Application.Abstractions;

namespace Meloberry.Wrapper.Desktop.Win32;

/// <summary>Native Windows COM IFileOpenDialog / IFileSaveDialog wrapper (zero WinForms/WPF).</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeFileDialogs(Win32WindowHost windowHost) : INativeDialogService
{
    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_ALLOWMULTISELECT = 0x00000200;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    public Task<string?> PickFolderAsync(string title, string? initialDir = null, CancellationToken ct = default)
    {
        return windowHost.InvokeOnUiThreadAsync(() =>
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                dialog.SetTitle(title);
                dialog.GetOptions(out var opts);
                dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                TrySetInitialFolder(dialog, initialDir);

                var hr = dialog.Show(windowHost.Handle);
                if (hr == ERROR_CANCELLED)
                {
                    return null;
                }

                Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out var item);
                try
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                    return path;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(item);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(dialog);
            }
        }, ct);
    }

    public Task<IReadOnlyList<string>> PickFilesAsync(
        string title,
        string filterLabel,
        string filterPattern,
        bool allowMultiple = true,
        string? initialDir = null,
        CancellationToken ct = default)
    {
        return windowHost.InvokeOnUiThreadAsync<IReadOnlyList<string>>(() =>
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                dialog.SetTitle(title);
                dialog.GetOptions(out var opts);
                var flags = opts | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR;
                if (allowMultiple)
                {
                    flags |= FOS_ALLOWMULTISELECT;
                }

                dialog.SetOptions(flags);
                ApplySingleFilter(dialog, filterLabel, filterPattern);
                TrySetInitialFolder(dialog, initialDir);

                var hr = dialog.Show(windowHost.Handle);
                if (hr == ERROR_CANCELLED)
                {
                    return [];
                }

                Marshal.ThrowExceptionForHR(hr);
                dialog.GetResults(out var array);
                try
                {
                    array.GetCount(out var count);
                    var paths = new List<string>((int)count);
                    for (uint i = 0; i < count; i++)
                    {
                        array.GetItemAt(i, out var item);
                        try
                        {
                            item.GetDisplayName(SIGDN_FILESYSPATH, out var filePath);
                            if (!string.IsNullOrWhiteSpace(filePath))
                            {
                                paths.Add(filePath);
                            }
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(item);
                        }
                    }

                    return paths;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(array);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(dialog);
            }
        }, ct);
    }

    public Task<string?> SaveFileAsync(
        string title,
        string defaultFileName,
        string filterLabel,
        string filterExtension,
        string? initialDir = null,
        CancellationToken ct = default)
    {
        return windowHost.InvokeOnUiThreadAsync(() =>
        {
            var dialog = (IFileSaveDialog)new FileSaveDialogRCW();
            try
            {
                dialog.SetTitle(title);
                dialog.SetFileName(defaultFileName);
                dialog.SetDefaultExtension(filterExtension);
                dialog.GetOptions(out var opts);
                dialog.SetOptions(opts | FOS_OVERWRITEPROMPT | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                ApplySingleFilter(dialog, filterLabel, $"*.{filterExtension.TrimStart('.')}");
                TrySetInitialFolder(dialog, initialDir);

                var hr = dialog.Show(windowHost.Handle);
                if (hr == ERROR_CANCELLED)
                {
                    return null;
                }

                Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out var item);
                try
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                    return path;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(item);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(dialog);
            }
        }, ct);
    }

    private static void TrySetInitialFolder(IFileDialog dialog, string? initialDir)
    {
        if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
        {
            return;
        }

        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(initialDir, IntPtr.Zero, ref iid, out var folderItem) == 0 && folderItem is not null)
        {
            try
            {
                dialog.SetFolder(folderItem);
            }
            finally
            {
                Marshal.FinalReleaseComObject(folderItem);
            }
        }
    }

    private static void ApplySingleFilter(IFileDialog dialog, string label, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        var cleanPattern = pattern.Trim();
        var cleanLabel = string.IsNullOrWhiteSpace(label) ? "Files" : label;
        var specs = string.Equals(cleanPattern, "*.*", StringComparison.Ordinal)
            ? new[]
            {
                new COMDLG_FILTERSPEC { pszName = cleanLabel, pszSpec = cleanPattern }
            }
            : new[]
            {
                new COMDLG_FILTERSPEC { pszName = cleanLabel, pszSpec = cleanPattern },
                new COMDLG_FILTERSPEC { pszName = "All Files (*.*)", pszSpec = "*.*" }
            };
        dialog.SetFileTypes((uint)specs.Length, specs);
        dialog.SetFileTypeIndex(1);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW;

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialogRCW;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszSpec;
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        [PreserveSig] new int Show(IntPtr parent);
        new void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        new void SetFileTypeIndex(uint iFileType);
        new void GetFileTypeIndex(out uint piFileType);
        new void Advise(IntPtr pfde, out uint pdwCookie);
        new void Unadvise(uint dwCookie);
        new void SetOptions(uint fos);
        new void GetOptions(out uint pfos);
        new void SetDefaultFolder(IShellItem psi);
        new void SetFolder(IShellItem psi);
        new void GetFolder(out IShellItem ppsi);
        new void GetCurrentSelection(out IShellItem ppsi);
        new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        new void GetResult(out IShellItem ppsi);
        new void AddPlace(IShellItem psi, int fdap);
        new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        new void Close(int hr);
        new void SetClientGuid(ref Guid guid);
        new void ClearClientData();
        new void SetFilter(IntPtr pFilter);
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport, Guid("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog : IFileDialog
    {
        [PreserveSig] new int Show(IntPtr parent);
        new void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        new void SetFileTypeIndex(uint iFileType);
        new void GetFileTypeIndex(out uint piFileType);
        new void Advise(IntPtr pfde, out uint pdwCookie);
        new void Unadvise(uint dwCookie);
        new void SetOptions(uint fos);
        new void GetOptions(out uint pfos);
        new void SetDefaultFolder(IShellItem psi);
        new void SetFolder(IShellItem psi);
        new void GetFolder(out IShellItem ppsi);
        new void GetCurrentSelection(out IShellItem ppsi);
        new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        new void GetResult(out IShellItem ppsi);
        new void AddPlace(IShellItem psi, int fdap);
        new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        new void Close(int hr);
        new void SetClientGuid(ref Guid guid);
        new void ClearClientData();
        new void SetFilter(IntPtr pFilter);
        void SetSaveAsItem(IShellItem psi);
        void SetProperties(IntPtr pStore);
        void SetCollectedProperties(IntPtr pList, bool fAppendDefault);
        void GetProperties(out IntPtr ppStore);
        void ApplyProperties(IShellItem psi, IntPtr pStore, IntPtr hwnd, IntPtr pSink);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }
}
