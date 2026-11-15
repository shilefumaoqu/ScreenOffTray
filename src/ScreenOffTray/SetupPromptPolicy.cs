using System;

namespace ScreenOffTray
{
    internal enum SetupNotice { None, Introduction, Attention }

    // Presentation history is separate from measured display-readiness evidence.
    // This class never enables display off, prepares a display, or reads input.
    internal sealed class SetupPromptPolicy
    {
        internal bool IntroShown { get; private set; }
        internal string LastNoticeKey { get; private set; }
        // A persisted notice is not proof that the user saw this launch fail.
        // Wait for the first settled, presentable result before consuming this.
        private bool firstCheckPending = true;

        internal SetupPromptPolicy(bool introShown, string lastNoticeKey, bool previouslyPrepared)
        {
            IntroShown = introShown || previouslyPrepared;
            LastNoticeKey = lastNoticeKey ?? "";
        }

        internal SetupNotice Observe(bool settled, bool ready, bool busy, string attentionKey)
        {
            if (!settled || busy) return SetupNotice.None;
            if (ready) { firstCheckPending = false; IntroShown = true; LastNoticeKey = ""; return SetupNotice.None; }
            string key = attentionKey ?? "";
            if (!IntroShown) {
                firstCheckPending = false; IntroShown = true; LastNoticeKey = key;
                return SetupNotice.Introduction;
            }
            if (firstCheckPending && !String.IsNullOrEmpty(key)) {
                firstCheckPending = false; LastNoticeKey = key;
                return SetupNotice.Attention;
            }
            if (String.IsNullOrEmpty(key) || String.Equals(key, LastNoticeKey, StringComparison.Ordinal)) return SetupNotice.None;
            LastNoticeKey = key;
            return SetupNotice.Attention;
        }

        internal void MarkIntroductionShown() { IntroShown = true; }

        // Closing the initial check means "later", including when its read-only
        // result has not arrived yet. This only dismisses presentation, never
        // marks the display profile ready. Verified readiness ends the episode.
        internal void DeferPendingCheck()
        { AcknowledgeVisibleState(false, "display-setup-required-v1"); }

        internal void AcknowledgeVisibleState(bool ready, string attentionKey)
        {
            firstCheckPending = false;
            IntroShown = true;
            LastNoticeKey = ready ? "" : (attentionKey ?? "");
        }

        internal static string AttentionKey(DisplaySetupResult result, string preparationError)
        {
            // An unresolved display-setup condition is one episode. Native
            // outcome text/profile availability may change while it remains
            // unresolved, and preparation errors are not retained across restart.
            // Only verified readiness ends the episode and permits a new notice.
            return result == null ? "" : "display-setup-required-v1";
        }
    }

    internal sealed class SetupPromptHistoryWriter
    {
        private bool pending;
        internal void Save(bool inMemoryMatches, Action assignValues, Action save)
        {
            if (!pending && inMemoryMatches) return;
            pending = true;
            assignValues(); save();
            pending = false;
        }
    }
}
