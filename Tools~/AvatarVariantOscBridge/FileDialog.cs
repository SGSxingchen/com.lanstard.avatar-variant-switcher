using System.Runtime.InteropServices;
using System.Text;

namespace AvatarVariantOscBridge;

/// <summary>
/// Win32 GetOpenFileNameW 的最小封装。原生 API 不走反射，AOT 直接可用。
/// </summary>
internal static class FileDialog
{
    public static string? PickMappingFile(string title, string? initialPath)
    {
        var fileBuffer = new StringBuilder(260);
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
        {
            fileBuffer.Append(initialPath);
        }

        // 过滤器格式："显示文本\0通配\0显示文本\0通配\0\0"
        var filter = "Avatar Variant Map (*.json)\0*.json\0All files (*.*)\0*.*\0\0";

        var ofn = new OpenFileName
        {
            lStructSize = Marshal.SizeOf<OpenFileName>(),
            hwndOwner = IntPtr.Zero,
            lpstrFilter = filter,
            lpstrFile = fileBuffer,
            nMaxFile = fileBuffer.Capacity,
            lpstrTitle = title,
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR
        };

        if (!GetOpenFileNameW(ref ofn))
        {
            return null; // 用户取消或对话框打开失败
        }

        var result = fileBuffer.ToString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER      = 0x00080000;
    private const int OFN_NOCHANGEDIR   = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int    lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrCustomFilter;
        public int    nMaxCustFilter;
        public int    nFilterIndex;
        public StringBuilder lpstrFile;
        public int    nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFileTitle;
        public int    nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string  lpstrTitle;
        public int    Flags;
        public short  nFileOffset;
        public short  nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int    dwReserved;
        public int    FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);
}
