namespace RcloneMount.UI
{
    // Central place for hard-coded layout numbers so a future "widen the window"
    // change (like the 1000 → 1120 bump earlier) touches one constant instead of
    // 40 scattered positions. Only Settings-tab metrics live here for now; other
    // tabs still use inline literals and can migrate here opportunistically.
    public static class LayoutMetrics
    {
        // Main window
        public const int WindowClientWidth  = 1120;
        public const int WindowClientHeight = 640;

        // The tab content area (inside tabHost, which sits at (12, 12) with a
        // 12-px right/bottom gutter to the form edge)
        public const int TabPadding = 12;
        public const int TabHostWidth  = WindowClientWidth  - TabPadding * 2;   // 1096

        // Card grid
        public const int CardGutter    = 12;
        public const int CardFullWidth = TabHostWidth - CardGutter * 2;         // 1072
        public const int CardHalfWidth = (CardFullWidth - CardGutter) / 2;      // 530

        // Vertical rhythm inside a settings-tab section
        public const int SectionHeaderY   = 12;   // first section header at top of the tab
        public const int SectionHeaderGap = 28;   // header → first card
        public const int SectionGap       = 20;   // last card in a section → next header
        public const int CardInnerPadLeft = 15;

        // Status strip
        public const int StatusStripHeight = 24;
    }
}
