namespace Vesper.App;

public sealed record ShellPreferences(bool PaneOpen = true, bool SystemAccent = false, bool HideUnavailable = true,
    bool SingingSeekEnabled = true, bool LinkRightPanels = true, double RightSplitRatio = 0.382,
    bool SegmentedLevelMeter = true, double BgmVolume = 60, double VocalVolume = 20);
