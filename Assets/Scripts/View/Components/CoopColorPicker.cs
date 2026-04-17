using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// HSV color picker for co-op player color. Built entirely in code (no UXML).
/// Uses three <see cref="SnapSlider"/>s (Hue, Saturation, Value) for visual
/// consistency with the rest of the settings UI. No snap/lock — just smooth
/// track+handle+value+buttons. Hex text field rounds it out.
///
/// Instead of a standalone preview swatch, the picker fires an immediate
/// <see cref="OnPreview"/> callback on every change — the consumer (AccountManager)
/// tints the display name text in real time, which doubles as an intuitive
/// contrast check against the settings background.
///
/// <see cref="OnCommit"/> is debounced (500 ms) for server persistence.
/// </summary>
public class CoopColorPicker
{
    public VisualElement Root { get; }

    /// <summary>Fired immediately on every color change. Use for live UI preview (e.g. tinting text).</summary>
    public event Action<Color> OnPreview;

    /// <summary>Fired (debounced 500 ms) when the color settles. Argument is "#RRGGBB". Use for server save.</summary>
    public event Action<string> OnCommit;

    private readonly SnapSlider _hueSlider;
    private readonly SnapSlider _satSlider;
    private readonly SnapSlider _valSlider;
    private readonly TextField _hexField;

    private IVisualElementScheduledItem _debounce;
    private bool _suppressEvents;

    public CoopColorPicker()
    {
        Root = new VisualElement();
        Root.AddToClassList("coop-color-picker");

        var titleLabel = new Label("Your color");
        titleLabel.AddToClassList("coop-color-picker__label");
        Root.Add(titleLabel);

        var sliders = new VisualElement();
        sliders.AddToClassList("coop-color-picker__sliders");
        Root.Add(sliders);

        _hueSlider = MakeSnapSlider(sliders, "H", 0, 360, 180);
        _satSlider = MakeSnapSlider(sliders, "S", 0, 100, 50);
        _valSlider = MakeSnapSlider(sliders, "V", 0, 100, 50);

        _hueSlider.OnValueChanged += _ => OnSliderChanged();
        _satSlider.OnValueChanged += _ => OnSliderChanged();
        _valSlider.OnValueChanged += _ => OnSliderChanged();

        var hexRow = new VisualElement();
        hexRow.AddToClassList("coop-color-picker__hex-row");
        Root.Add(hexRow);

        var hashLabel = new Label("#");
        hashLabel.AddToClassList("coop-color-picker__hash");
        hexRow.Add(hashLabel);

        _hexField = new TextField { maxLength = 6 };
        _hexField.AddToClassList("coop-color-picker__hex-field");
        _hexField.AddToClassList("labeled-field__input");
        _hexField.RegisterCallback<FocusOutEvent>(_ => OnHexCommit());
        _hexField.RegisterCallback<KeyDownEvent>(e =>
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                OnHexCommit();
        });
        hexRow.Add(_hexField);

        // Default: middle of the palette
        SetValueWithoutNotify("#3498DB");
    }

    /// <summary>
    /// Set the picker to a hex color without firing OnCommit or OnPreview.
    /// Returns the parsed Color for the caller to apply externally if needed.
    /// </summary>
    public Color SetValueWithoutNotify(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return Color.white;
        if (!hex.StartsWith("#"))
            hex = "#" + hex;

        if (!ColorUtility.TryParseHtmlString(hex, out var color))
            return Color.white;

        _suppressEvents = true;
        Color.RGBToHSV(color, out float h, out float s, out float v);
        _hueSlider.SetValueWithoutNotify(Mathf.Round(h * 360f));
        _satSlider.SetValueWithoutNotify(Mathf.Round(s * 100f));
        _valSlider.SetValueWithoutNotify(Mathf.Round(v * 100f));
        _hexField.SetValueWithoutNotify(ColorUtility.ToHtmlStringRGB(color));
        _suppressEvents = false;

        return color;
    }

    /// <summary>Current hex value as "#RRGGBB".</summary>
    public string CurrentHex => "#" + ColorUtility.ToHtmlStringRGB(CurrentColor);

    /// <summary>Current color as a Unity Color.</summary>
    public Color CurrentColor =>
        Color.HSVToRGB(_hueSlider.Value / 360f, _satSlider.Value / 100f, _valSlider.Value / 100f);

    /// <summary>
    /// Returns focus items for FocusNavigator integration. Each slider track
    /// supports horizontal keyboard stepping.
    /// </summary>
    public List<FocusNavigator.FocusItem> GetFocusItems()
    {
        var items = new List<FocusNavigator.FocusItem>();

        items.Add(MakeSliderFocusItem(_hueSlider));
        items.Add(MakeSliderFocusItem(_satSlider));
        items.Add(MakeSliderFocusItem(_valSlider));
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _hexField,
                OnActivate = () =>
                {
                    _hexField.Focus();
                    return true;
                },
            }
        );

        return items;
    }

    // -- Internals -----------------------------------------------------------

    private void OnSliderChanged()
    {
        if (_suppressEvents)
            return;

        var color = CurrentColor;
        _suppressEvents = true;
        _hexField.SetValueWithoutNotify(ColorUtility.ToHtmlStringRGB(color));
        _suppressEvents = false;
        OnPreview?.Invoke(color);
        ScheduleCommit();
    }

    private void OnHexCommit()
    {
        var text = _hexField.value.Trim();
        if (text.Length != 6)
            return;
        if (!ColorUtility.TryParseHtmlString("#" + text, out var color))
            return;

        _suppressEvents = true;
        Color.RGBToHSV(color, out float h, out float s, out float v);
        _hueSlider.SetValueWithoutNotify(Mathf.Round(h * 360f));
        _satSlider.SetValueWithoutNotify(Mathf.Round(s * 100f));
        _valSlider.SetValueWithoutNotify(Mathf.Round(v * 100f));
        _suppressEvents = false;
        OnPreview?.Invoke(color);
        ScheduleCommit();
    }

    private void ScheduleCommit()
    {
        _debounce?.Pause();
        _debounce = Root.schedule.Execute(() => OnCommit?.Invoke(CurrentHex)).StartingIn(500);
    }

    private static SnapSlider MakeSnapSlider(
        VisualElement parent,
        string label,
        float min,
        float max,
        float initial
    )
    {
        var row = new VisualElement();
        row.AddToClassList("coop-color-picker__slider-row");

        var nameLabel = new Label(label);
        nameLabel.AddToClassList("coop-color-picker__slider-name");
        row.Add(nameLabel);

        var slider = new SnapSlider(
            min,
            max,
            initial,
            smallStep: 1f,
            snapStep: 0f,
            format: "0",
            showLock: false
        );
        row.Add(slider.Root);

        parent.Add(row);
        return slider;
    }

    private static FocusNavigator.FocusItem MakeSliderFocusItem(SnapSlider slider)
    {
        return new FocusNavigator.FocusItem
        {
            Element = slider.Track,
            CustomFocusVisual = true,
            OnHorizontal = dir =>
            {
                slider.KeyboardStep(dir, false);
                return true;
            },
        };
    }
}
