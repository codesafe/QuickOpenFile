using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace QuickOpenFile
{
    /// <summary>
    /// Visual Assist "Open File in Solution" 대화상자.
    /// 중앙: 솔루션의 전체 파일 목록 / 하단: 필터 입력창.
    /// Enter 또는 더블클릭으로 선택한 파일을 연다.
    /// </summary>
    internal sealed class QuickOpenDialog : DialogWindow
    {
        // 대화상자를 다시 열었을 때 마지막 필터를 복원한다 (Visual Assist 와 동일한 동작).
        private static string s_lastFilter = string.Empty;
        private static double s_lastWidth = 960;
        private static double s_lastHeight = 560;

        private readonly Func<bool, Task<IReadOnlyList<SolutionFileEntry>>> _loadFiles;
        private readonly ListView _list;
        private readonly TextBox _filterBox;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _filterTimer;

        private IReadOnlyList<SolutionFileEntry> _allFiles = Array.Empty<SolutionFileEntry>();

        /// <param name="loadFiles">파일 목록 로더. 인자가 true 이면 캐시를 무시하고 다시 수집한다.</param>
        public QuickOpenDialog(Func<bool, Task<IReadOnlyList<SolutionFileEntry>>> loadFiles)
        {
            _loadFiles = loadFiles;

            Title = "Open File in Solution";
            Width = s_lastWidth;
            Height = s_lastHeight;
            MinWidth = 400;
            MinHeight = 250;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            HasMinimizeButton = false;
            HasMaximizeButton = true;

            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(this, true);
            SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);

            // ---- 파일 목록 ----
            _list = new ListView
            {
                SelectionMode = SelectionMode.Extended,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(8, 8, 8, 4),
            };
            _list.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _list.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _list.SetResourceReference(BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetIsDeferredScrollingEnabled(_list, false);

            // 상단 고정 헤더: 이름 / 파일 위치 / 프로젝트
            var grid = new GridView { ColumnHeaderContainerStyle = CreateColumnHeaderStyle(), AllowsColumnReorder = false };
            grid.Columns.Add(new GridViewColumn { Header = "이름", Width = 260, CellTemplate = CreateGridCellTemplate(nameof(SolutionFileEntry.FileName)) });
            grid.Columns.Add(new GridViewColumn { Header = "파일 위치", Width = 480, CellTemplate = CreateGridCellTemplate(nameof(SolutionFileEntry.Directory)) });
            grid.Columns.Add(new GridViewColumn { Header = "프로젝트", Width = 160, CellTemplate = CreateGridCellTemplate(nameof(SolutionFileEntry.Project)) });
            _list.View = grid;

            // VS 테마 스타일이 ListView 템플릿을 바꿔 헤더가 사라지는 일이 없도록,
            // GridView 전용 ScrollViewer(헤더 줄이 스크롤 영역 위에 고정됨)를 쓰는 템플릿을 직접 지정한다.
            _list.Template = CreateListTemplate();

            // 행 구분선: 각 항목 아래쪽 테두리
            var itemStyle = new Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            itemStyle.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.GridLineBrushKey)));
            itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(TemplateProperty, CreateItemTemplate()));

            // 선택 색: WPF 기본 색은 다크 테마에서 글자가 보이지 않으므로 VS 테마 색을 쓴다.
            // 포커스가 대부분 입력창에 있으므로 활성 선택 색을 항상 사용한다.
            var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension(TreeViewColors.SelectedItemActiveBrushKey)));
            selectedTrigger.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(TreeViewColors.SelectedItemActiveTextBrushKey)));
            itemStyle.Triggers.Add(selectedTrigger);
            itemStyle.Setters.Add(new EventSetter(MouseDoubleClickEvent, new MouseButtonEventHandler(OnItemDoubleClick)));
            itemStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(SolutionFileEntry.FullPath))));
            _list.ItemContainerStyle = itemStyle;

            _list.PreviewKeyDown += OnListPreviewKeyDown;
            _list.PreviewTextInput += OnListPreviewTextInput;

            // ---- 하단 입력창 ----
            _filterBox = new TextBox
            {
                Margin = new Thickness(8, 4, 8, 8),
                Padding = new Thickness(2, 3, 2, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _filterBox.TextChanged += (s, e) => { _filterTimer.Stop(); _filterTimer.Start(); };
            _filterBox.PreviewKeyDown += OnFilterPreviewKeyDown;

            _status = new TextBlock
            {
                Margin = new Thickness(0, 4, 10, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.8,
            };

            var hint = new TextBlock
            {
                Text = "공백: AND   -단어: 제외   \\ 포함: 경로 검색   Enter: 열기   F5: 새로고침",
                Margin = new Thickness(8, 0, 8, 2),
                FontSize = 11,
                Opacity = 0.65,
            };

            var bottom = new DockPanel();
            DockPanel.SetDock(_status, Dock.Right);
            bottom.Children.Add(_status);
            bottom.Children.Add(_filterBox);

            var root = new DockPanel();
            var bottomStack = new StackPanel();
            bottomStack.Children.Add(hint);
            bottomStack.Children.Add(bottom);
            DockPanel.SetDock(bottomStack, Dock.Bottom);
            root.Children.Add(bottomStack);
            root.Children.Add(_list);
            Content = root;

            _filterTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            _filterTimer.Tick += (s, e) => { _filterTimer.Stop(); ApplyFilter(); };

            Loaded += OnLoaded;
            Closing += (s, e) =>
            {
                s_lastFilter = _filterBox.Text;
                if (WindowState == WindowState.Normal)
                {
                    s_lastWidth = ActualWidth;
                    s_lastHeight = ActualHeight;
                }
            };
            PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        private static ControlTemplate CreateListTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));

            var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
            scrollViewer.SetResourceReference(StyleProperty, GridView.GridViewScrollViewerStyleKey);
            scrollViewer.SetValue(FocusableProperty, false);
            scrollViewer.SetValue(ScrollViewer.CanContentScrollProperty, true); // 가상화 유지

            scrollViewer.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            border.AppendChild(scrollViewer);

            return new ControlTemplate(typeof(ListView)) { VisualTree = border };
        }

        /// <summary>항목 템플릿: 배경 + 아래쪽 구분선 + 컬럼별 셀.</summary>
        private static ControlTemplate CreateItemTemplate()
        {
            var rowPresenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
            rowPresenter.SetValue(GridViewRowPresenter.ColumnsProperty, new TemplateBindingExtension(GridView.ColumnCollectionProperty));
            rowPresenter.SetValue(GridViewRowPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);
            border.AppendChild(rowPresenter);

            return new ControlTemplate(typeof(ListViewItem)) { VisualTree = border };
        }

        /// <summary>
        /// 헤더 칸:굵은 제목 + 행/열 구분선 + 폭 조절 손잡이(PART_HeaderGripper).
        /// </summary>
        private static Style CreateColumnHeaderStyle()
        {
            var gripperBorder = new FrameworkElementFactory(typeof(Border));
            gripperBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var gripperTemplate = new ControlTemplate(typeof(Thumb)) { VisualTree = gripperBorder };

            var gripper = new FrameworkElementFactory(typeof(Thumb), "PART_HeaderGripper");
            gripper.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            gripper.SetValue(WidthProperty, 8.0);
            gripper.SetValue(MarginProperty, new Thickness(0, 0, -4, 0));
            gripper.SetValue(CursorProperty, Cursors.SizeWE);
            gripper.SetValue(TemplateProperty, gripperTemplate);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(MarginProperty, new Thickness(6, 4, 6, 4));
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.GridLineBrushKey);
            border.AppendChild(presenter);

            var root = new FrameworkElementFactory(typeof(Grid));
            root.AppendChild(border);
            root.AppendChild(gripper);

            var style = new Style(typeof(GridViewColumnHeader));
            style.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = root }));
            style.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension(HeaderColors.DefaultBrushKey)));
            style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(HeaderColors.DefaultTextBrushKey)));
            style.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
            return style;
        }

        /// <summary>
        /// 열 구분선이 있는 셀 템플릿.
        /// GridViewRowPresenter 가 셀 좌우에 6px 여백을 두므로 음수 Margin 으로 셀 경계까지 늘린 뒤 오른쪽 테두리를 그린다.
        /// </summary>
        private static DataTemplate CreateGridCellTemplate(string propertyName)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 0));
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.GridLineBrushKey);
            border.SetValue(MarginProperty, new Thickness(-6, 0, -6, 0));

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(propertyName));
            text.SetValue(TextBlock.PaddingProperty, new Thickness(6, 3, 6, 3));
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            border.AppendChild(text);

            return new DataTemplate { VisualTree = border };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _filterBox.Text = s_lastFilter;
            _filterBox.SelectAll();
            _filterBox.Focus();
            _ = ReloadAsync(force: false);
        }

        private async Task ReloadAsync(bool force)
        {
            _status.Text = "Loading...";
            try
            {
                _allFiles = await _loadFiles(force);
            }
            catch (Exception ex)
            {
                _status.Text = "Error: " + ex.Message;
                return;
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            _filterTimer.Stop();
            List<SolutionFileEntry> filtered = FileFilter.Apply(_allFiles, _filterBox.Text);
            _list.ItemsSource = filtered;
            _status.Text = $"{filtered.Count:N0} / {_allFiles.Count:N0}";

            if (filtered.Count > 0)
            {
                _list.SelectedIndex = 0;
                _list.ScrollIntoView(filtered[0]);
            }
        }

        #region 키보드 / 마우스

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    DialogResult = false;
                    e.Handled = true;
                    break;
                case Key.F5:
                    _ = ReloadAsync(force: true);
                    e.Handled = true;
                    break;
            }
        }

        private void OnFilterPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    if (_filterTimer.IsEnabled)
                    {
                        ApplyFilter(); // 입력 직후 바로 Enter 를 누른 경우
                    }

                    OpenSelected();
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    MoveSelection(PageSize());
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    MoveSelection(-PageSize());
                    e.Handled = true;
                    break;
                case Key.Home when Keyboard.Modifiers == ModifierKeys.Control:
                    MoveSelection(int.MinValue / 2);
                    e.Handled = true;
                    break;
                case Key.End when Keyboard.Modifiers == ModifierKeys.Control:
                    MoveSelection(int.MaxValue / 2);
                    e.Handled = true;
                    break;
            }
        }

        private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                _filterBox.Focus();
                _filterBox.CaretIndex = _filterBox.Text.Length;
            }
        }

        // 목록에 포커스가 있을 때 글자를 입력하면 입력창으로 보낸다.
        private void OnListPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
            {
                return;
            }

            _filterBox.Focus();
            _filterBox.SelectedText = e.Text;
            _filterBox.CaretIndex = _filterBox.SelectionStart + _filterBox.SelectionLength;
            _filterBox.SelectionLength = 0;
            e.Handled = true;
        }

        private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                OpenSelected();
                e.Handled = true;
            }
        }

        private void MoveSelection(int delta)
        {
            int count = _list.Items.Count;
            if (count == 0)
            {
                return;
            }

            int index = Math.Max(0, Math.Min(count - 1, (_list.SelectedIndex < 0 ? 0 : _list.SelectedIndex) + delta));
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _list.SelectedIndex >= 0)
            {
                // Shift + 방향키: 여러 파일 선택
                int anchor = _list.SelectedIndex;
                _list.SelectedItems.Clear();
                for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
                {
                    _list.SelectedItems.Add(_list.Items[i]);
                }
            }
            else
            {
                _list.SelectedIndex = index;
            }

            _list.ScrollIntoView(_list.Items[index]);
        }

        private int PageSize()
        {
            if (_list.Items.Count > 0 && _list.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) is FrameworkElement item &&
                item.ActualHeight > 0)
            {
                return Math.Max(1, (int)(_list.ActualHeight / item.ActualHeight) - 2);
            }

            return 10;
        }

        #endregion

        private void OpenSelected()
        {
            List<SolutionFileEntry> selected = _list.SelectedItems.Cast<SolutionFileEntry>().ToList();
            if (selected.Count == 0 && _list.Items.Count > 0)
            {
                selected.Add((SolutionFileEntry)_list.Items[0]);
            }

            if (selected.Count == 0)
            {
                return;
            }

            SelectedFiles = selected;
            DialogResult = true;
        }

        /// <summary>대화상자가 true 로 닫혔을 때 열어야 할 파일들.</summary>
        public IReadOnlyList<SolutionFileEntry> SelectedFiles { get; private set; } = Array.Empty<SolutionFileEntry>();
    }
}
