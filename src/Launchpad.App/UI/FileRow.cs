using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Launchpad.App.Native;
using Launchpad.Core.Search;

namespace Launchpad.App.UI;

/// <summary>One line in the file results list (lightweight; the icon is fetched lazily when the row is realised).</summary>
public sealed class FileRow
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public FileRow(SearchHit hit, string[] terms)
    {
        Name = hit.Name;
        Path = hit.Path;
        Directory = hit.Directory;
        IsDirectory = hit.IsDirectory;
        Extension = hit.Extension;
        Terms = terms;
        SizeText = hit.IsDirectory ? "" : FormatSize(hit.Size);
        DateText = hit.Modified == DateTime.MinValue ? "" : hit.Modified.ToString("MMM d, yyyy  HH:mm");
    }

    public string Name { get; }
    public string Path { get; }
    public string Directory { get; }
    public bool IsDirectory { get; }
    public string Extension { get; }
    public IReadOnlyList<string> Terms { get; }
    public string SizeText { get; }
    public string DateText { get; }

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _icon = ShellIcons.ForFile(Path, Extension, IsDirectory); _iconLoaded = true; }
            return _icon;
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

/// <summary>TextBlock that emphasises the parts of <see cref="Source"/> matching any of <see cref="Terms"/>.</summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(HighlightTextBlock), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TermsProperty = DependencyProperty.Register(
        nameof(Terms), typeof(IEnumerable<string>), typeof(HighlightTextBlock), new PropertyMetadata(null, OnChanged));

    private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xC9, 0xFF)));

    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public IEnumerable<string>? Terms { get => (IEnumerable<string>?)GetValue(TermsProperty); set => SetValue(TermsProperty, value); }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((HighlightTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        string text = Source ?? "";
        var terms = Terms;
        if (text.Length == 0) return;
        if (terms == null || !terms.Any()) { Inlines.Add(new Run(text)); return; }

        var mark = new bool[text.Length];
        bool any = false;
        foreach (string t in terms)
        {
            if (t.Length == 0) continue;
            int from = 0;
            while (from < text.Length)
            {
                int i = text.IndexOf(t, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                for (int k = i; k < i + t.Length; k++) mark[k] = true;
                any = true;
                from = i + t.Length;
            }
        }
        if (!any) { Inlines.Add(new Run(text)); return; }

        int pos = 0;
        while (pos < text.Length)
        {
            int end = pos;
            while (end < text.Length && mark[end] == mark[pos]) end++;
            var run = new Run(text[pos..end]);
            if (mark[pos]) { run.FontWeight = FontWeights.SemiBold; run.Foreground = HighlightBrush; }
            Inlines.Add(run);
            pos = end;
        }
    }
}
