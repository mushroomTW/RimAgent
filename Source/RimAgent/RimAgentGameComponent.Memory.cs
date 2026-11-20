#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;
using Verse;

namespace RimAgent;

public partial class RimAgentGameComponent
{
    /// <summary>全域記憶檔：跨存檔共用，玩家可直接檢視、備份或手動編輯。</summary>
    internal static string GlobalMemoryPath
    {
        get { return Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent", "Memory", GlobalMemoryFile.FileName); }
    }

    /// <summary>全域記憶；第一次取用時從磁碟載入，讀不了的檔案改名保留再從空白開始。</summary>
    private AgentMemoryStore GlobalMemory
    {
        get
        {
            if (_globalMemory == null)
            {
                _globalMemory = GlobalMemoryFile.Load(GlobalMemoryPath, out string warning);
                if (warning != null) Log.Warning("RimAgent " + warning);
                if (_globalMemory.DroppedOnLoad > 0)
                    Log.Warning("RimAgent discarded " + _globalMemory.DroppedOnLoad.ToString(CultureInfo.InvariantCulture) + " global memory file(s) that exceed the current limits.");
            }
            return _globalMemory;
        }
    }

    /// <summary>agent 看到的一組記憶：以檔名把局內記憶與全域記憶分到兩個範圍。</summary>
    private ScopedMemoryStore Memory
    {
        get
        {
            if (_scopedMemory == null) _scopedMemory = new ScopedMemoryStore(_saveMemory, GlobalMemory, SaveGlobalMemory);
            return _scopedMemory;
        }
    }

    /// <summary>開新局或讀檔時忘掉全域記憶快取：玩家可能改過檔案，別的存檔也可能剛寫過。</summary>
    private void ResetMemory()
    {
        _globalMemory = null;
        _scopedMemory = null;
    }

    /// <summary>寫回全域記憶；寫不了就讓工具回報失敗，不假裝記住了。</summary>
    private void SaveGlobalMemory()
    {
        AgentMemoryStore global = _globalMemory ?? GlobalMemory;
        try
        {
            GlobalMemoryFile.Save(GlobalMemoryPath, global.Snapshot());
            _pendingGlobalMigration.Clear();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new InvalidOperationException("global memory could not be saved: " + ex.Message, ex);
        }
    }

    /// <summary>把先前版本留在存檔裡的全域記憶併入全域記憶（同名者以磁碟上的為準）；回傳被捨棄的筆數。</summary>
    private int MergeIntoGlobalMemory(List<AgentMemoryEntry> migrated)
    {
        List<AgentMemoryEntry> merged = GlobalMemory.Snapshot();
        merged.AddRange(migrated);
        _globalMemory = new AgentMemoryStore(merged);
        try
        {
            SaveGlobalMemory();
        }
        catch (InvalidOperationException ex)
        {
            // 讀檔不能因為寫不回磁碟而失敗：這批記憶留在記憶體裡，下次寫入再試。
            Log.Warning("RimAgent could not persist migrated global memory: " + ex.Message);
        }
        return _globalMemory.DroppedOnLoad;
    }
}
