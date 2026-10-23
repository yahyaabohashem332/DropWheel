using System.Windows;
using System.Windows.Input;
using Petal.App.Localization;
using Petal.Core.Text;

namespace Petal.App.Overlay;

/// <summary>
/// A calculator in the wheel's centre: with the wheel open from the keyboard, typing = starts a sum
/// ("12% of 340", "5 km in mi", "sqrt 2"), the centre shows the answer as it is typed, Enter copies it, Esc
/// goes back to the petals.
/// </summary>
public sealed partial class WheelWindow
{
    // The sum being typed; null while the centre isn't a calculator.
    private string? _calc;

    public bool IsCalculating => _calc is not null;

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (!_interactive || string.IsNullOrEmpty(e.Text))
            return;
        if (_calc is null)
        {
            if (e.Text != "=")
                return;
            _calc = "";
            CloseOrbit();
            SetHighlight(null, null);
            ShowCalc();
            e.Handled = true;
            return;
        }
        foreach (var c in e.Text)
            if (!char.IsControl(c) && _calc.Length < 60)
                _calc += c;
        ShowCalc();
        e.Handled = true;
    }

    /// <summary>The keys a sum needs before they reach the petals: digits type, Backspace deletes, Enter copies, Esc leaves.</summary>
    private bool CalcKey(Key key, KeyEventArgs e)
    {
        if (_calc is null)
            return false;
        switch (key)
        {
            case Key.Escape:
                EndCalc();
                Render(animate: false);
                break;
            case Key.Back:
                if (_calc.Length > 0)
                    _calc = _calc[..^1];
                ShowCalc();
                break;
            case Key.Enter:
                if (Calculator.Evaluate(_calc) is { } answer)
                {
                    try { Clipboard.SetText(answer.Text); } catch (System.Runtime.InteropServices.ExternalException) { }
                    Sound.Drop();
                    CloseWheel();
                }
                break;
            case Key.Tab:
                break;
            default:
                // Letters, digits and signs arrive as text.
                return true;
        }
        e.Handled = true;
        return true;
    }

    private void ShowCalc()
    {
        var sum = _calc ?? "";
        if (sum.Trim().Length == 0)
        {
            SetHub("=", L.T("Type a sum: 12% of 340, 5 km in mi"), false);
            return;
        }
        var answer = Calculator.Evaluate(sum);
        var shown = answer is null ? "…" : "= " + answer.Shown;
        // A left-to-right mark keeps "12% of 340" in its order inside an Arabic centre.
        var written = LeftToRight + sum;
        SetHub(LeftToRight + shown, answer is null ? written : written + " · " + L.T("Enter copies"), false);
    }

    private void EndCalc() => _calc = null;

    private static readonly string LeftToRight = char.ConvertFromUtf32(0x200E);
}
