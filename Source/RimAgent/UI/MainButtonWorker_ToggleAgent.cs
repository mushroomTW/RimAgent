using RimWorld;
using Verse;

namespace RimAgent.UI;

#pragma warning disable S101 // Follows RimWorld MainButtonWorker naming convention and matches Def XML reference
public class MainButtonWorker_ToggleAgent : MainButtonWorker
#pragma warning restore S101
{
    public override void Activate()
    {
        Window_Agent.Toggle();
    }

    public override float ButtonBarPercent => Window_Agent.IsOpenNow ? 1f : 0f;
}
