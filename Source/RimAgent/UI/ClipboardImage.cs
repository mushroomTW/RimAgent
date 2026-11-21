using System;
using System.Runtime.InteropServices;
using RimAgent.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RimAgent.UI;

/// <summary>Unity IMGUI 只能貼文字，因此以 Win32 API 讀剪貼簿圖片；只能在主線程呼叫。</summary>
internal static class ClipboardImage
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;

#pragma warning disable SYSLIB1054 // 避免在 Mod 組件中開啟 AllowUnsafeBlocks，維持 DllImport 與 safe 指標封送
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);
#pragma warning restore SYSLIB1054

    private static bool IsWindows =>
        Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

    public static byte[] TryRead()
    {
        if (!IsWindows) return [];
        try
        {
            if (IsClipboardFormatAvailable(CfUnicodeText)) return [];
            uint png = RegisterClipboardFormat("PNG");
            bool hasPng = png != 0 && IsClipboardFormatAvailable(png);
            if (!hasPng && !IsClipboardFormatAvailable(CfDib)) return [];
            if (!OpenClipboard(IntPtr.Zero)) return [];
            byte[] data;
            try
            {
                data = ReadFormat(hasPng ? png : CfDib);
            }
            finally
            {
                CloseClipboard();
            }
            if (data == null) return [];
            try
            {
                return hasPng ? ModelImageEncoder.FromEncoded(data) : FromDib(data);
            }
            catch (Exception)
            {
                // 剪貼簿內容是外部資料，解碼失敗（壞檔、不支援的格式）就當作沒有圖片，不讓例外冒到 UI。
                return [];
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    private const ulong MaxClipboardBytes = 256UL * 1024 * 1024;

    private static byte[] ReadFormat(uint format)
    {
        IntPtr handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return [];
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return [];
        try
        {
            ulong size = GlobalSize(handle).ToUInt64();
            if (size == 0 || size > MaxClipboardBytes) return [];
            var data = new byte[(int)size];
            Marshal.Copy(pointer, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    internal static byte[] FromDib(byte[] dib)
    {
        if (!DibDecoder.TryDecode(dib, out int width, out int height, out byte[] rgba)) return [];
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
        try
        {
            texture.LoadRawTextureData(rgba);
            texture.Apply();
            return ModelImageEncoder.FromTexture(texture);
        }
        finally
        {
            Object.Destroy(texture);
        }
    }
}
