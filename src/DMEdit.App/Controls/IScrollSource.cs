namespace DMEdit.App.Controls;

/// <summary>
/// Read-only view of scroll state that a scrollbar can bind to.
/// The scrollbar reads these values on every render — no duplicated
/// state, single source of truth.
/// </summary>
public interface IScrollSource {
    /// <summary>Maximum scroll offset (extent − viewport). Always ≥ 0.</summary>
    double ScrollMaximum { get; }

    /// <summary>Current vertical scroll offset (0 .. ScrollMaximum).</summary>
    double ScrollValue { get; }

    /// <summary>Viewport height in pixels.</summary>
    double ScrollViewportHeight { get; }

    /// <summary>Total content extent height in pixels.</summary>
    double ScrollExtentHeight { get; }

    /// <summary>Height of a single visual row in pixels.</summary>
    double RowHeightValue { get; }
}
