using System.Collections;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Obsidian.Controls;

/// <summary>One rendered line of the hex viewer (16 bytes).</summary>
public sealed class HexRow
{
    public long Offset { get; init; }
    public string OffsetText { get; init; } = string.Empty;
    public string HexText { get; init; } = string.Empty;
    public string AsciiText { get; init; } = string.Empty;
}

/// <summary>
/// Lazily materialised row source for <see cref="HexView"/>. WPF's virtualising
/// panel only asks for the visible indices, so multi-megabyte buffers stay cheap.
/// </summary>
public sealed class HexRowSource : IList<HexRow>
{
    private readonly byte[] _data;
    private readonly int _length;

    public HexRowSource(byte[] data, int length)
    {
        _data = data;
        _length = Math.Min(length, data.Length);
        Count = (_length + 15) / 16;
    }

    public int Count { get; }

    public HexRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            long offset = (long)index * 16;
            int rowLength = (int)Math.Min(16, _length - offset);
            var hex = new StringBuilder(48);
            var ascii = new StringBuilder(16);

            for (int i = 0; i < 16; i++)
            {
                if (i < rowLength)
                {
                    byte value = _data[offset + i];
                    hex.Append(value.ToString("X2")).Append(' ');
                    ascii.Append(value is >= 32 and < 127 ? (char)value : '.');
                }
                else
                {
                    hex.Append("   ");
                    ascii.Append(' ');
                }
            }

            return new HexRow
            {
                Offset = offset,
                OffsetText = offset.ToString("X8", CultureInfo.InvariantCulture),
                HexText = hex.ToString(),
                AsciiText = ascii.ToString(),
            };
        }
        set => throw new NotSupportedException();
    }

    public bool IsReadOnly => true;
    public bool Contains(HexRow item) => false;
    public int IndexOf(HexRow item) => -1;
    public void CopyTo(HexRow[] array, int arrayIndex) { }
    public IEnumerator<HexRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(HexRow item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, HexRow item) => throw new NotSupportedException();
    public bool Remove(HexRow item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

public partial class HexView : UserControl
{
    public static readonly DependencyProperty BytesProperty = DependencyProperty.Register(
        nameof(Bytes), typeof(byte[]), typeof(HexView),
        new PropertyMetadata(null, OnBytesChanged));

    public static readonly DependencyProperty SourceLabelProperty = DependencyProperty.Register(
        nameof(SourceLabel), typeof(string), typeof(HexView),
        new PropertyMetadata(string.Empty));

    private const int MaxDisplayBytes = 8 * 1024 * 1024;
    private byte[] _data = Array.Empty<byte>();
    private int _length;
    private int _lastSearch = -1;

    public HexView()
    {
        InitializeComponent();
        Rows.SelectionChanged += (_, _) => UpdateStatus();
    }

    public byte[]? Bytes
    {
        get => (byte[]?)GetValue(BytesProperty);
        set => SetValue(BytesProperty, value);
    }

    public string SourceLabel
    {
        get => (string)GetValue(SourceLabelProperty);
        set => SetValue(SourceLabelProperty, value);
    }

    private static void OnBytesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not HexView view)
            return;

        view._data = args.NewValue as byte[] ?? Array.Empty<byte>();
        view._length = Math.Min(view._data.Length, MaxDisplayBytes);
        view.Rows.ItemsSource = view._length == 0 ? null : new HexRowSource(view._data, view._length);
        view.EmptyState.Visibility = view._length == 0 ? Visibility.Visible : Visibility.Collapsed;
        view._lastSearch = -1;
        view.UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (StatusText == null)
            return;

        if (_data.Length == 0)
        {
            StatusText.Text = "empty";
            return;
        }

        string truncated = _length < _data.Length ? $" · showing first {MaxDisplayBytes / (1024 * 1024)} MB" : string.Empty;
        long selected = Rows.SelectedItem is HexRow row ? row.Offset : 0;
        StatusText.Text = $"{_data.Length:N0} bytes{truncated} · 0x{selected:X8}";
    }

    private void Find_Click(object sender, RoutedEventArgs e) => FindNext();

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
    }

    private void FindNext()
    {
        if (_length == 0)
            return;

        string query = FindBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        (byte[]? pattern, bool[]? mask, string? error) = BuildPattern(query, HexMode.IsChecked == true);
        if (error != null)
        {
            StatusText.Text = error;
            return;
        }

        int found = Search(_data, _length, pattern!, mask, _lastSearch + 1);
        if (found < 0 && _lastSearch >= 0)
            found = Search(_data, _length, pattern!, mask, 0);

        if (found < 0)
        {
            StatusText.Text = "not found";
            _lastSearch = -1;
            return;
        }

        _lastSearch = found;
        SelectOffset(found);
        StatusText.Text = $"found at 0x{found:X8}";
    }

    private void Go_Click(object sender, RoutedEventArgs e) => GoToOffset();

    private void GoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoToOffset();
            e.Handled = true;
        }
    }

    private void GoToOffset()
    {
        string text = GoBox.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long offset) &&
            !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
        {
            StatusText.Text = "invalid offset";
            return;
        }

        SelectOffset(offset);
    }

    private void SelectOffset(long offset)
    {
        if (_length == 0)
            return;

        if (offset < 0)
            offset = 0;
        if (offset >= _length)
            offset = _length - 1;

        int index = (int)(offset / 16);
        if (index >= 0 && index < Rows.Items.Count)
        {
            Rows.SelectedIndex = index;
            Rows.ScrollIntoView(Rows.SelectedItem);
        }

        UpdateStatus();
    }

    private static (byte[]? Pattern, bool[]? Mask, string? Error) BuildPattern(string query, bool hex)
    {
        if (!hex)
            return (Encoding.UTF8.GetBytes(query), null, null);

        string[] tokens = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var mask = new bool[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "??" or "?")
            {
                mask[i] = true;
                continue;
            }

            // Exactly one byte per token (same rule as MemoryScanner.Compile):
            // a longer token such as "4D5A" previously slipped through
            // byte.TryParse as a single byte and silently searched wrong data.
            if (tokens[i].Length != 2)
                return (null, null, $"'{tokens[i]}' is not a hex byte (use two hex digits like 4D, or ??).");
            if (!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return (null, null, $"'{tokens[i]}' is not a hex byte.");
        }

        return (bytes, mask, null);
    }

    private static int Search(byte[] data, int length, byte[] pattern, bool[]? mask, int start)
    {
        if (pattern.Length == 0 || start < 0)
            return -1;

        int last = length - pattern.Length;
        for (int i = start; i <= last; i++)
        {
            if (mask?[0] != true && data[i] != pattern[0])
                continue;

            int j = 1;
            for (; j < pattern.Length; j++)
            {
                if (mask?[j] == true)
                    continue;
                if (data[i + j] != pattern[j])
                    break;
            }

            if (j == pattern.Length)
                return i;
        }

        return -1;
    }
}
