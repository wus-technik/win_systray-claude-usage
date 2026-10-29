namespace ClaudeUsageTray.Tray;

/// <summary>The badge's silhouette identifies the provider. Lives beside the renderer because no
/// pure decision depends on it. Colour is severity, the digit is the window, arc direction is the
/// period, dimming is staleness, and the bottom-right corner is the outage marker; silhouette is
/// the channel that survives Windows reordering the tray.</summary>
public enum BadgeShape { Disc, RoundedSquare }
