using System;

namespace RimAgent.Agent;

/// <summary>
/// 會話正在處理上一句指令，這次請求沒有被受理。
/// 繼承 <see cref="InvalidOperationException"/> 保持既有語意；呼叫端用這個型別分辨「忙碌」與其他失敗，
/// 不靠一般的 InvalidOperationException（框架或其他元件也可能丟同型別）。
/// </summary>
public sealed class AgentBusyException : InvalidOperationException
{
    public AgentBusyException()
        : base("RimAgent is still working on the previous instruction.")
    {
    }
}
