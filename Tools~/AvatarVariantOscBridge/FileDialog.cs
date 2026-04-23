using System.Runtime.InteropServices;

namespace AvatarVariantOscBridge;

/// <summary>
/// Win32 GetOpenFileNameW 的最小封装。原生 API 不走反射，AOT 直接可用。
/// </summary>
internal static class FileDialog
{
    // 支持长路径（Windows 10+ 支持 32767 字符）；MAX_PATH=260 在今天太紧。
    private const int BufferCharCount = 32 * 1024;

    public static string? PickMappingFile(string title, string? initialPath)
    {
        // 手动分配非托管缓冲区：StringBuilder 不允许作为 P/Invoke struct 字段。
        var bufferBytes = BufferCharCount * sizeof(char);
        var buffer = Marshal.AllocHGlobal(bufferBytes);
        try
        {
            // 以空字符串起始
            Marshal.WriteInt16(buffer, 0, 0);

            if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
            {
                var chars = initialPath!.ToCharArray();
                var copyLen = Math.Min(chars.Length, BufferCharCount - 1);
                for (int i = 0; i < copyLen; i++)
                    Marshal.WriteInt16(buffer, i * sizeof(char), chars[i]);
                Marshal.WriteInt16(buffer, copyLen * sizeof(char), 0);
            }

            // 过滤器格式："显示文本\0通配\0显示文本\0通配\0\0"
            var filter = "Avatar Variant Map (*.json)\0*.json\0All files (*.*)\0*.*\0\0";

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = IntPtr.Zero,
                lpstrFilter = filter,
                lpstrFile = buffer,
                nMaxFile = BufferCharCount,
                lpstrTitle = title,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR
            };

            if (!GetOpenFileNameW(ref ofn))
            {
                // 0 表示用户取消；非 0 表示真的出错了（CDERR_* 或 FNERR_*）。
                var err = CommDlgExtendedError();
                if (err != 0)
                {
                    Console.Error.WriteLine($"WARN: file dialog failed (code 0x{err:X}).");
                }
                return null;
            }

            var result = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
        public IntPtr lpstrFile;
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

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();
}
