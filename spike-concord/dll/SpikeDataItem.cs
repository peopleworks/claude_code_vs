// Throwaway Concord Rung-0 probe (see HelloWorldService.cs).

using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;

namespace ConcordSpike
{
    internal enum State
    {
        /// <summary>Start of a stack walk - sentinel not yet injected.</summary>
        Initial,
        /// <summary>Sentinel frame already injected for this walk.</summary>
        FrameAdded
    }

    /// <summary>
    /// Per-stack-walk state store. Concord lets a component hang a private DkmDataItem off a
    /// Dkm object (here the DkmStackContext for one stack walk); it's how we remember that the
    /// sentinel was already added so we don't inject it on every frame.
    /// </summary>
    internal class SpikeDataItem : DkmDataItem
    {
        private SpikeDataItem() { }

        public State State { get; set; }

        public static SpikeDataItem GetInstance(DkmStackContext context)
        {
            SpikeDataItem item = context.GetDataItem<SpikeDataItem>();
            if (item != null)
                return item;

            item = new SpikeDataItem();
            context.SetDataItem<SpikeDataItem>(DkmDataCreationDisposition.CreateNew, item);
            return item;
        }
    }
}
