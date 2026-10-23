using Petal.App.Input;
using Petal.App.Localization;

namespace Petal.App.Overlay;

/// <summary>
/// Steps in one go: while dragging, Space on a petal makes it a step (with the keyboard, +), the wheel
/// then offers what fits the files as they will be, and the drop on the last petal runs every step in
/// order: "WEBP", then "Compress", then "ZIP". Backspace takes the last step back.
/// </summary>
public sealed partial class WheelWindow
{
    /// <summary>More steps than this are a recipe's job.</summary>
    public const int MostSteps = 8;

    private readonly List<WheelItem> _chain = [];

    /// <summary>Why a petal can't follow these steps for these files; null when it can.</summary>
    public Func<IReadOnlyList<WheelItem>, WheelItem, IReadOnlyList<string>, string?>? ChainCheck { get; set; }

    /// <summary>The files' names as they will be after these steps, for choosing the petals offered next.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<WheelItem>, IReadOnlyList<string>>? ChainView { get; set; }

    /// <summary>A drop that ends a chain: its steps, the petal dropped on, the files and where.</summary>
    public event Action<IReadOnlyList<WheelItem>, WheelItem, IReadOnlyList<string>, ScreenPoint>? ChainDropped;

    /// <summary>The steps chosen so far.</summary>
    public IReadOnlyList<WheelItem> Chain => _chain;

    private IReadOnlyList<string> ContentFiles(IReadOnlyList<string> files) =>
        _chain.Count > 0 && ChainView is { } view ? view(files, _chain) : files;

    /// <summary>The petal under the pointer (or the lit bubble of an open group) becomes the next step.</summary>
    public void AddStep()
    {
        if (!_open || _pages is not null || _files is not { } files || _content is not { } content)
            return;
        WheelItem? item = _orbit is { Highlight: { } k } orbit && k < orbit.Bubbles.Count ? orbit.Bubbles[k].Item
            : _highlight is { } i && i < content.Items.Count ? content.Items[i] : null;
        if (item is null || item.IsGroup || item.Unavailable is not null && item.Blocked)
            return;
        var why = _chain.Count >= MostSteps ? L.F("Up to {0} steps", MostSteps)
            : ChainCheck is { } check ? check(_chain, item, files) is { } reason ? L.T(reason) : null
            : L.T("This can't be a step");
        if (why is not null)
        {
            SetHub(HubTitle(item), why, animate: IsVisible);
            return;
        }
        _chain.Add(item);
        Sound.Drop();
        StartAfterStep();
    }

    /// <summary>Takes the last step back.</summary>
    public void RemoveStep()
    {
        if (!_open || _chain.Count == 0)
            return;
        _chain.RemoveAt(_chain.Count - 1);
        Sound.Tick();
        StartAfterStep();
    }

    /// <summary>The wheel again, for the files as the steps leave them, with the steps in the centre.</summary>
    private void StartAfterStep()
    {
        CancelPreview();
        _dwellTimer.Stop();
        _groups.Clear();
        ResetOrbit();
        _highlight = null;
        Render(animate: false);
        ShowDefaultHub(animate: IsVisible);
    }

    /// <summary>"WEBP → Compress", the last few steps when there are many.</summary>
    private string ChainTitle()
    {
        var names = _chain.Select(HubTitle).ToList();
        return names.Count <= 3 ? string.Join(" → ", names) : "… → " + string.Join(" → ", names.TakeLast(3));
    }

    private string ChainHint() => L.T(_interactive ? "+ adds a step · Enter ends here" : "Space adds a step · drop on the last one");
}
