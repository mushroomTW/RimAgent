using System;

namespace RimAgent.Agent;

/// <summary>會話正在處理上一句指令，這次請求未被受理；獨立型別用來與框架或其他元件也可能丟出的 InvalidOperationException 區分。</summary>
public sealed class AgentBusyException : InvalidOperationException
{
    public AgentBusyException()
        : base("RimAgent is still working on the previous instruction.")
    {
    }
}
