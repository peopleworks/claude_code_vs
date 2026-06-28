// Throwaway Concord Rung-0 probe. Adapted from microsoft/ConcordExtensibilitySamples
// HelloWorld/Cs (MIT). The ONLY question this answers: does VS 2026 load an unsigned,
// third-party managed Concord component at all? If the sentinel frame below shows up at
// the top of the Call Stack window during ANY managed debug session, the answer is yes.

using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;

namespace ConcordSpike
{
    /// <summary>
    /// A Concord call-stack filter. The component is registered (vsdconfig) to implement
    /// IDkmCallStackFilter; the engine calls FilterNextFrame once per frame as it walks a
    /// stack, letting us rewrite the frame list. We inject one annotated sentinel frame at
    /// the very top and pass every real frame through untouched.
    ///
    /// The interface list here MUST match ConcordSpike.vsdconfigxml.
    /// </summary>
    public class HelloWorldService : IDkmCallStackFilter
    {
        DkmStackWalkFrame[] IDkmCallStackFilter.FilterNextFrame(DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            // A null input frame marks the end of the stack walk - nothing to add there.
            if (input == null)
                return null;

            // Per-stack-walk state, so we only inject the sentinel on the top-most frame.
            SpikeDataItem dataItem = SpikeDataItem.GetInstance(stackContext);
            DkmStackWalkFrame[] frames;

            if (dataItem.State == State.Initial)
            {
                // Top frame: return [sentinel, realTopFrame].
                frames = new DkmStackWalkFrame[2];
                frames[0] = DkmStackWalkFrame.Create(
                    stackContext.Thread,
                    null,                       // annotated frame - no instruction address
                    input.FrameBase,            // reuse the real frame's base
                    0,                          // annotated frame occupies zero bytes
                    DkmStackWalkFrameFlags.None,
                    "[ClaudeCodeVS Concord Spike]",   // <-- the canary text we look for
                    null,
                    null);
                frames[1] = input;
                dataItem.State = State.FrameAdded;
            }
            else
            {
                // Already injected for this walk - pass the real frame straight through.
                frames = new DkmStackWalkFrame[1] { input };
            }

            return frames;
        }
    }
}
