using System;
using System.Runtime.InteropServices;
using RimAgent.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RimAgent.UI;

/// <summary>
/// 從 Windows 剪貼簿讀取圖片並轉成給模型看的 JPEG。Unity IMGUI 只能貼文字，所以直接呼叫 Win32 API。
/// 優先讀「PNG」格式（截圖工具、瀏覽器複製圖片），沒有再讀 CF_DIB（PrintScreen）。只能在主線程呼叫。
/// </summary>
internal static class ClipboardImage
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;

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

    private static bool IsWindows =>
        Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

    /// <summary>
    /// 剪貼簿有圖片且沒有文字時讀出並轉成 JPEG；有文字時交給輸入框照常貼上。
    /// </summary>
    /// <returns>JPEG 位元組；沒有可用的圖片或非 Windows 時回傳 null。</returns>
    public static byte[] TryRead()
    {
        if (!IsWindows) return null;
        try
        {
            if (IsClipboardFormatAvailable(CfUnicodeText)) return null;
            uint png = RegisterClipboardFormat("PNG");
            bool hasPng = png != 0 && IsClipboardFormatAvailable(png);
            if (!hasPng && !IsClipboardFormatAvailable(CfDib)) return null;
            if (!OpenClipboard(IntPtr.Zero)) return null;
            byte[] data;
            try
            {
                data = ReadFormat(hasPng ? png : CfDib);
            }
            finally
            {
                CloseClipboard();
            }
            if (data == null) return null;
            return hasPng ? ModelImageEncoder.FromEncoded(data) : FromDib(data);
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static byte[] ReadFormat(uint format)
    {
        IntPtr handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            ulong size = GlobalSize(handle).ToUInt64();
            if (size == 0 || size > int.MaxValue) return null;
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
        if (!DibDecoder.TryDecode(dib, out int width, out int height, out byte[] rgba)) return null;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
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
