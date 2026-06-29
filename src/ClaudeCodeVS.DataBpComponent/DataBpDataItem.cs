using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;

namespace ClaudeCodeVs.DataBp
{
    internal enum State { Initial, FrameAdded }

    /// <summary>Per-stack-walk state (Concord DkmDataItem), so the load-canary frame is injected once.</summary>
    internal sealed class DataBpDataItem : DkmDataItem
    {
        private DataBpDataItem() { }

        public State State { get; set; }

        public static DataBpDataItem GetInstance(DkmStackContext context)
        {
            DataBpDataItem item = context.GetDataItem<DataBpDataItem>();
            if (item != null) return item;
            item = new DataBpDataItem();
            context.SetDataItem<DataBpDataItem>(DkmDataCreationDisposition.CreateNew, item);
            return item;
        }
    }
}
