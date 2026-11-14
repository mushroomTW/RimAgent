using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimAgent.Game;

/// <summary>
/// 一張進行中的離屏俯視截圖，掛在自己專用的攝影機物件上。
/// <para>
/// 生命週期全由物件本身負責：截完、逾時或讀檔換場景時物件被刪，<see cref="OnDestroy"/> 一定會釋放渲染目標並退出登記，
/// 不會留下影響玩家畫面的狀態；多張截圖可同時進行。
/// </para>
/// <para>
/// 地圖網格與動態物件平常只畫玩家鏡頭內的部分。進行中的截圖範圍以 Harmony 併進
/// <c>MapDrawer.ViewRect</c> 與 <c>DynamicDrawManager.ComputeCulledThings</c> 的繪製範圍，只改「畫哪些格子」，
/// 區域、特效、剪影等其他繪製照常，玩家畫面不受影響。
/// </para>
/// </summary>
internal sealed class MapShotCapture : MonoBehaviour
{
    /// <summary>攝影機架在地圖上方的高度，與原版重力船截圖相同。</summary>
    private const float CameraHeight = 15f;

    /// <summary>
    /// 第幾次渲染後取圖。開始的那一幀，地圖可能在登記前就送出網格了；等下一幀整幀都在擴大的範圍下再取。
    /// </summary>
    private const int RendersBeforeCapture = 2;

    /// <summary>超過這麼多幀還沒取到圖就放棄（例如切到世界地圖時攝影機不渲染），以免一直擴大繪製範圍。</summary>
    private const int StaleFrames = 120;

    private static readonly List<MapShotCapture> Active = new();
    private static bool _patched;

    private Map _map;
    private CellRect _bounds;
    private MapShotLayout _layout;
    private string _path;
    private Camera _camera;
    private RenderTexture _target;
    private int _renders;
    private int _startFrame;

    /// <summary>開始一張截圖；檔案在之後一兩幀渲染完才寫到 <paramref name="path"/>。須在主線程呼叫。</summary>
    public static void Begin(Map map, MapShotLayout layout, string path)
    {
        EnsurePatched();
        var shot = new GameObject("RimAgentMapShot").AddComponent<MapShotCapture>();
        shot.Init(map, layout, path);
    }

    private void Init(Map map, MapShotLayout layout, string path)
    {
        _map = map;
        _layout = layout;
        _path = path;
        _bounds = new CellRect(layout.Rect.MinX, layout.Rect.MinZ, layout.CellsWide, layout.CellsHigh);
        _startFrame = Time.frameCount;

        _camera = gameObject.AddComponent<Camera>();
        _camera.CopyFrom(Find.Camera);
        _camera.cullingMask &= ~LayerMask.GetMask("UI");
        _camera.Fit(_bounds, CameraHeight);
        _target = RenderTexture.GetTemporary(layout.MapWidthPx, layout.MapHeightPx, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
        _camera.targetTexture = _target;

        Active.Add(this);
        Camera.onPostRender += OnAnyPostRender;
    }

    private void OnAnyPostRender(Camera camera)
    {
        if (camera != _camera || ++_renders < RendersBeforeCapture) return;
        Camera.onPostRender -= OnAnyPostRender;
        try
        {
            // 這兩幀之間玩家切了地圖或打開世界地圖，畫出來的就不是要的那張；不寫檔，讀圖端會逾時放棄。
            if (Find.CurrentMap == _map && WorldRendererUtility.DrawingMap) Save();
            else Log.Warning("RimAgent: map screenshot skipped because the shown map changed before it rendered.");
        }
        catch (Exception ex)
        {
            Log.Error("RimAgent: map screenshot failed: " + ex);
        }
        finally
        {
            Destroy(gameObject);
        }
    }

    private void Save()
    {
        Texture2D rendered = null;
        Texture2D composed = null;
        try
        {
            rendered = _target.CreateTexture2D(TextureFormat.RGBA32);
            byte[] pixels = MapShotOverlay.Compose(rendered.GetRawTextureData(), _layout);
            composed = new Texture2D(_layout.ImageWidth, _layout.ImageHeight, TextureFormat.RGBA32, false);
            composed.LoadRawTextureData(pixels);
            composed.Apply();
            File.WriteAllBytes(_path, composed.EncodeToPNG());
        }
        finally
        {
            if (rendered != null) Destroy(rendered);
            if (composed != null) Destroy(composed);
        }
    }

    private void Update()
    {
        if (Time.frameCount - _startFrame <= StaleFrames) return;
        Log.Warning("RimAgent: map screenshot gave up after " + StaleFrames + " frames without rendering.");
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        Camera.onPostRender -= OnAnyPostRender;
        Active.Remove(this);
        if (_camera != null) _camera.targetTexture = null;
        if (_target != null)
        {
            RenderTexture.ReleaseTemporary(_target);
            _target = null;
        }
    }

    /// <summary>這張地圖上進行中截圖的聯集範圍；沒有則為 null。</summary>
    private static CellRect? PendingBounds(Map map)
    {
        CellRect? union = null;
        foreach (MapShotCapture shot in Active)
        {
            if (shot._map != map) continue;
            union = union.HasValue ? union.Value.Encapsulate(shot._bounds) : shot._bounds;
        }
        return union;
    }

    /// <summary>第一次截圖時才打補丁，沒用到這功能的玩家不受任何影響。</summary>
    private static void EnsurePatched()
    {
        if (_patched) return;
        _patched = true;
        var harmony = new Harmony("greenmushroom.rimagent.mapshot");
        harmony.Patch(AccessTools.PropertyGetter(typeof(MapDrawer), "ViewRect"),
            postfix: new HarmonyMethod(typeof(MapShotCapture), nameof(ViewRectPostfix)));
        harmony.Patch(AccessTools.Method(typeof(DynamicDrawManager), "ComputeCulledThings"),
            prefix: new HarmonyMethod(typeof(MapShotCapture), nameof(CullPrefix)),
            finalizer: new HarmonyMethod(typeof(MapShotCapture), nameof(CullFinalizer)));
    }

    /// <summary>地圖網格（含需要重建的區塊）也畫進行中截圖的範圍。</summary>
    private static void ViewRectPostfix(Map ___map, ref CellRect __result)
    {
        if (Active.Count == 0) return;
        CellRect? pending = PendingBounds(___map);
        if (pending.HasValue) __result = __result.Encapsulate(pending.Value).ClipInsideMap(___map);
    }

    /// <summary>
    /// 原版只在這個方法裡用重力船截圖的旗標放大動態物件的範圍，沒有其他掛點；
    /// 這裡只在方法執行期間開旗標（併上原版自己的範圍），結束立刻還原，旗標的其他用途不受影響。
    /// </summary>
    private static void CullPrefix(Map ___map, out (bool On, CellRect Bounds, bool Changed) __state)
    {
        __state = (GravshipCapturer.IsGravshipRenderInProgress, GravshipCapturer.GravshipCaptureBounds, false);
        if (Active.Count == 0) return;
        CellRect? pending = PendingBounds(___map);
        if (!pending.HasValue) return;
        GravshipCapturer.GravshipCaptureBounds = __state.On ? __state.Bounds.Encapsulate(pending.Value) : pending.Value;
        GravshipCapturer.IsGravshipRenderInProgress = true;
        __state.Changed = true;
    }

    private static void CullFinalizer((bool On, CellRect Bounds, bool Changed) __state)
    {
        if (!__state.Changed) return;
        GravshipCapturer.IsGravshipRenderInProgress = __state.On;
        GravshipCapturer.GravshipCaptureBounds = __state.Bounds;
    }
}
