using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace QuickOpenFile.NavigationBar
{
    /// <summary>
    /// Visual Assist "List Methods in File" 와 같은 팝업.
    /// 상단 입력창으로 필터링하고 Enter / 더블클릭으로 해당 위치로 이동한다.
    /// </summary>
    internal sealed class MethodListPopup : Popup
    {
        private sealed class Row
        {
            public Row(CodeSymbol symbol, string text)
            {
                Symbol = symbol;
                Text = text;
            }

            public CodeSymbol Symbol { get; }

            public string Text { get; }

            public override string ToString() => Text;
        }

        private readonly List<Row> _rows;
        private readonly TextBox _filterBox;
        private readonly ListBox _list;
        private readonly Action<CodeSymbol> _navigate;
        private readonly CodeSymbol _current;

        public MethodListPopup(IReadOnlyList<CodeSymbol> symbols, CodeSymbol current, Action<CodeSymbol> navigate)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _navigate = navigate;
            _current = current;
            _rows = symbols
                .Where(s => s.Kind != CodeSymbolKind.Namespace)
                .Select(s => new Row(s, new string(' ', Math.Max(0, IndentLevel(s)) * 4) + s.GetDisplayText()))
                .ToList();

            StaysOpen = false;
            AllowsTransparency = false;
            Focusable = true;

            _filterBox = new TextBox { Margin = new Thickness(4), Padding = new Thickness(2) };
            _filterBox.TextChanged += (s, e) => ApplyFilter();
            _filterBox.PreviewKeyDown += OnFilterPreviewKeyDown;

            _list = new ListBox
            {
                MaxHeight = 420,
                MinWidth = 420,
                BorderThickness = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            };
            _list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _list.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            _list.MouseDoubleClick += (s, e) => NavigateToSelection();
            _list.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    NavigateToSelection();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    IsOpen = false;
                    e.Handled = true;
                }
            };

            var itemStyle = new Style(typeof(ListBoxItem));
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(TreeViewColors.SelectedItemActiveBrushKey)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TreeViewColors.SelectedItemActiveTextBrushKey)));
            itemStyle.Triggers.Add(selected);
            _list.ItemContainerStyle = itemStyle;

            var panel = new DockPanel();
            DockPanel.SetDock(_filterBox, Dock.Top);
            panel.Children.Add(_filterBox);
            panel.Children.Add(_list);

            var border = new Border { BorderThickness = new Thickness(1), Child = panel };
            border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Child = border;

            Opened += (s, e) =>
            {
                ApplyFilter();
                _filterBox.Focus();
                Keyboard.Focus(_filterBox);
            };
        }

        private static int IndentLevel(CodeSymbol symbol)
        {
            int level = 0;
            for (CodeSymbol p = symbol.Parent; p != null; p = p.Parent)
            {
                if (p.Kind == CodeSymbolKind.Type)
                {
                    level++;
                }
            }

            return level;
        }

        private void ApplyFilter()
        {
            string[] tokens = _filterBox.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<Row> filtered = _rows
                .Where(r => tokens.All(t => r.Text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            _list.ItemsSource = filtered;

            Row select = tokens.Length == 0 ? filtered.FirstOrDefault(r => r.Symbol == _current) : null;
            _list.SelectedItem = select ?? filtered.FirstOrDefault();
            if (_list.SelectedItem != null)
            {
                _list.ScrollIntoView(_list.SelectedItem);
            }
        }

        private void OnFilterPreviewKeyDown(object sender, KeyEventArgs e)
        {
            int count = _list.Items.Count;
            switch (e.Key)
            {
                case Key.Down when count > 0:
                    _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 1);
                    break;
                case Key.Up when count > 0:
                    _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                    break;
                case Key.PageDown when count > 0:
                    _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + 15);
                    break;
                case Key.PageUp when count > 0:
                    _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 15);
                    break;
                case Key.Enter:
                    NavigateToSelection();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    IsOpen = false;
                    e.Handled = true;
                    return;
                default:
                    return;
            }

            if (_list.SelectedItem != null)
            {
                _list.ScrollIntoView(_list.SelectedItem);
            }

            e.Handled = true;
        }

        private void NavigateToSelection()
        {
            if (_list.SelectedItem is Row row)
            {
                IsOpen = false;
                _navigate(row.Symbol);
            }
        }
    }
}
