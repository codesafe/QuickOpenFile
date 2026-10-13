using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Task = System.Threading.Tasks.Task;

namespace QuickOpenFile.NavigationBar
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(NavigationBarMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.Top)]
    [ContentType("C/C++")]
    [ContentType("CSharp")]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class NavigationBarMarginProvider : IWpfTextViewMarginProvider
    {
        [Import]
        internal IAsyncQuickInfoBroker QuickInfoBroker { get; set; }

        [Import]
        internal SVsServiceProvider ServiceProvider { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new NavigationBarMargin(wpfTextViewHost.TextView, QuickInfoBroker, (DTE)ServiceProvider.GetService(typeof(DTE)));
        }
    }

    /// <summary>
    /// Visual Assist Navigation Bar.
    /// <list type="bullet">
    /// <item>Context 칸: 커서가 있는 범위 (예: <c>Foo::Bar</c>). 클릭하면 파일의 함수 목록.</item>
    /// <item>Definition 칸: 커서 위치 심볼의 선언 (예: <c>int m_count</c>).</item>
    /// <item>Go 버튼: Alt+G 와 같은 선언부 ↔ 구현부 이동.</item>
    /// </list>
    /// <para>
    /// UI 스레드를 막지 않도록:
    /// 범위 정보는 <see cref="ScopeParser"/> 로 백그라운드 스레드에서 계산하고 (VS 코드 모델 사용 안 함),
    /// 화면에 보이는 편집기에서만 갱신하며,
    /// Quick Info 조회는 솔루션 로드가 끝난 뒤 포커스를 가진 편집기에서만 한다.
    /// </para>
    /// </summary>
    internal sealed class NavigationBarMargin : IWpfTextViewMargin
    {
        public const string MarginName = "QuickOpenFile.NavigationBar";

        private static readonly TimeSpan CaretDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ParseDelay = TimeSpan.FromMilliseconds(500);

        private readonly IWpfTextView _view;
        private readonly IAsyncQuickInfoBroker _quickInfoBroker;
        private readonly DTE _dte;
        private readonly SourceLanguage _language;

        private readonly Border _root;
        private readonly TextBlock _contextText;
        private readonly TextBlock _definitionText;
        private readonly Border _contextField;

        private readonly DispatcherTimer _caretTimer;
        private readonly DispatcherTimer _parseTimer;

        private List<CodeSymbol> _symbols;
        private int _parsedVersion = -1;
        private CancellationTokenSource _parseCts;
        private CancellationTokenSource _definitionCts;
        private MethodListPopup _popup;
        private bool _waitingForSolution;
        private bool _disposed;

        public NavigationBarMargin(IWpfTextView view, IAsyncQuickInfoBroker quickInfoBroker, DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _view = view;
            _quickInfoBroker = quickInfoBroker;
            _dte = dte;
            _language = view.TextBuffer.ContentType.IsOfType("CSharp") ? SourceLanguage.CSharp : SourceLanguage.Cpp;

            // ---- UI ----
            _contextText = CreateFieldText();
            _definitionText = CreateFieldText();

            var dropArrow = new TextBlock { Text = "▾", Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            var contextPanel = new DockPanel();
            DockPanel.SetDock(dropArrow, Dock.Right);
            contextPanel.Children.Add(dropArrow);
            contextPanel.Children.Add(_contextText);

            _contextField = CreateField(contextPanel);
            _contextField.Cursor = Cursors.Hand;
            _contextField.ToolTip = "현재 범위 — 클릭하면 파일의 함수 목록 (Alt+M)";
            _contextField.MouseLeftButtonUp += (s, e) => { ShowMethodList(); e.Handled = true; };

            Border definitionField = CreateField(_definitionText);

            var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };

            FrameworkElement goButton = CreateGoButton();

            // 배치: [Context 칸] [splitter] [Definition 칸] [Go]
            var grid = new Grid { Margin = new Thickness(2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 80 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 80 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_contextField, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(definitionField, 2);
            Grid.SetColumn(goButton, 3);
            grid.Children.Add(_contextField);
            grid.Children.Add(splitter);
            grid.Children.Add(definitionField);
            grid.Children.Add(goButton);

            _root = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1) };
            _root.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
            _root.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
            _root.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
            _root.Visibility = NavigationBarSettings.IsVisible ? Visibility.Visible : Visibility.Collapsed;

            // ---- 이벤트 ----
            _caretTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CaretDelay };
            _caretTimer.Tick += (s, e) => { _caretTimer.Stop(); Refresh(); };

            _parseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ParseDelay };
            _parseTimer.Tick += (s, e) => { _parseTimer.Stop(); StartParse(); };

            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.TextBuffer.PostChanged += OnBufferPostChanged;
            _view.GotAggregateFocus += OnGotFocus;
            _view.VisualElement.IsVisibleChanged += OnViewIsVisibleChanged;
            _view.Closed += OnViewClosed;
            NavigationBarSettings.VisibilityChanged += OnVisibilityChanged;

            _view.Properties[typeof(NavigationBarMargin)] = this;

            // 시작 시 복원된 뒤쪽 탭은 화면에 보일 때까지 아무 작업도 하지 않는다.
            ScheduleRefresh();
        }

        #region IWpfTextViewMargin

        public FrameworkElement VisualElement => _root;

        public double MarginSize => _root.ActualHeight;

        public bool Enabled => true;

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _caretTimer.Stop();
            _parseTimer.Stop();
            _parseCts?.Cancel();
            _definitionCts?.Cancel();
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.TextBuffer.PostChanged -= OnBufferPostChanged;
            _view.GotAggregateFocus -= OnGotFocus;
            _view.VisualElement.IsVisibleChanged -= OnViewIsVisibleChanged;
            _view.Closed -= OnViewClosed;
            NavigationBarSettings.VisibilityChanged -= OnVisibilityChanged;
            if (_waitingForSolution)
            {
                KnownUIContexts.SolutionExistsAndFullyLoadedContext.UIContextChanged -= OnSolutionFullyLoaded;
                _waitingForSolution = false;
            }

            _view.Properties.RemoveProperty(typeof(NavigationBarMargin));
            _symbols = null;
        }

        #endregion

        #region UI 구성

        private static TextBlock CreateFieldText() => new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 2, 5, 2),
        };

        private static Border CreateField(UIElement content)
        {
            var field = new Border { BorderThickness = new Thickness(1), Child = content, MinHeight = 20 };
            field.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            field.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ComboBoxBorderBrushKey);
            field.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ComboBoxTextBrushKey);
            return field;
        }

        /// <summary>
        /// Visual Assist 의 Go 버튼: Alt+G (Goto Implementation/Declaration) 와 같은 동작.
        /// 포커스를 가져가지 않으므로 클릭해도 편집기 커서 위치가 유지된다.
        /// </summary>
        private FrameworkElement CreateGoButton()
        {
            var icon = new CrispImage { Moniker = KnownMonikers.GoToDefinition, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = "Go", Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1, 6, 1) };
            content.Children.Add(icon);
            content.Children.Add(label);

            var button = new Border
            {
                Child = content,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = "Goto Implementation/Declaration (Alt+G)\n커서 위치 심볼의 선언부 ↔ 구현부로 이동",
            };
            button.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
            button.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);

            button.MouseEnter += (s, e) =>
            {
                button.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey);
                button.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarHoverOverSelectedIconBorderBrushKey);
            };
            button.MouseLeave += (s, e) =>
            {
                button.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
                button.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
            };
            button.MouseLeftButtonDown += (s, e) =>
            {
                button.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarMouseDownBackgroundBeginBrushKey);
                e.Handled = true;
            };
            button.MouseLeftButtonUp += (s, e) =>
            {
                button.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey);
                ThreadHelper.ThrowIfNotOnUIThread();
                e.Handled = true;
                ExecuteGo();
            };

            return button;
        }

        private void ExecuteGo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 이 편집기를 활성 문서로 만든 뒤 Alt+G 와 같은 명령을 실행한다.
            _view.VisualElement.Focus();
            _ = ExecuteGoAsync(); // UI 스레드에서 시작하며 예외는 메서드 안에서 모두 처리한다.
        }

        private async Task ExecuteGoAsync()
        {
            try
            {
                await GotoDeclImplCommand.ExecuteAsync(_dte, SetStatusBarText);
            }
            catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
            {
                System.Diagnostics.Debug.WriteLine($"QuickOpenFile NavigationBar Go: {ex}");
            }
        }

        private static void SetStatusBarText(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                statusBar.IsFrozen(out int frozen);
                if (frozen == 0)
                {
                    statusBar.SetText(text);
                }
            }
        }

        #endregion

        #region 갱신 시점

        /// <summary>막대가 켜져 있고 편집기가 실제로 화면에 보일 때만 작업한다.</summary>
        private bool IsShown => !_disposed && _root.Visibility == Visibility.Visible && _view.VisualElement.IsVisible;

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e) => ScheduleRefresh();

        private void OnGotFocus(object sender, EventArgs e) => ScheduleRefresh();

        private void OnViewIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ScheduleRefresh();

        private void OnViewClosed(object sender, EventArgs e) => Dispose();

        private void OnBufferPostChanged(object sender, EventArgs e)
        {
            // 입력이 멈춘 뒤 다시 파싱한다.
            _parseTimer.Stop();
            if (IsShown)
            {
                _parseTimer.Start();
            }
        }

        private void OnVisibilityChanged(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _root.Visibility = NavigationBarSettings.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            ScheduleRefresh();
        }

        private void OnSolutionFullyLoaded(object sender, UIContextChangedEventArgs e)
        {
            if (e.Activated)
            {
                KnownUIContexts.SolutionExistsAndFullyLoadedContext.UIContextChanged -= OnSolutionFullyLoaded;
                _waitingForSolution = false;
                ScheduleRefresh();
            }
        }

        private void ScheduleRefresh()
        {
            if (!IsShown)
            {
                return;
            }

            _caretTimer.Stop();
            _caretTimer.Start();
        }

        private void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsShown)
            {
                return;
            }

            if (_parsedVersion != _view.TextSnapshot.Version.VersionNumber && _parseCts == null)
            {
                StartParse();
            }

            UpdateContext();

            if (CanQueryQuickInfo())
            {
                _ = UpdateDefinitionAsync(); // UI 스레드에서 시작하며 예외는 메서드 안에서 모두 처리한다.
            }
        }

        /// <summary>
        /// Quick Info 는 언어 서비스(C++ IntelliSense / Roslyn)를 깨우므로
        /// 솔루션 로드가 끝난 뒤, 사용자가 실제로 보고 있는(포커스를 가진) 편집기에서만 조회한다.
        /// </summary>
        private bool CanQueryQuickInfo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_view.HasAggregateFocus)
            {
                return false;
            }

            if (KnownUIContexts.SolutionExistsAndFullyLoadedContext.IsActive ||
                KnownUIContexts.NoSolutionContext.IsActive ||
                KnownUIContexts.EmptySolutionContext.IsActive)
            {
                return true;
            }

            if (!_waitingForSolution)
            {
                _waitingForSolution = true;
                KnownUIContexts.SolutionExistsAndFullyLoadedContext.UIContextChanged += OnSolutionFullyLoaded;
            }

            return false;
        }

        #endregion

        #region Context 칸 (자체 파서, 백그라운드 스레드)

        private int CaretLine => _view.Caret.Position.BufferPosition.GetContainingLine().LineNumber + 1;

        private void StartParse()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsShown)
            {
                return;
            }

            _parseCts?.Cancel();
            var cts = new CancellationTokenSource();
            _parseCts = cts;
            ITextSnapshot snapshot = _view.TextSnapshot;
            _ = ParseAsync(snapshot, cts); // UI 스레드에서 시작하며 예외는 메서드 안에서 모두 처리한다.
        }

        private async Task ParseAsync(ITextSnapshot snapshot, CancellationTokenSource cts)
        {
            try
            {
                SourceLanguage language = _language;
                List<CodeSymbol> symbols = await Task.Run(() => ScopeParser.Parse(snapshot.GetText(), language), cts.Token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);

                if (_disposed || cts.IsCancellationRequested)
                {
                    return;
                }

                _symbols = symbols;
                _parsedVersion = snapshot.Version.VersionNumber;
                UpdateContext();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
            {
                System.Diagnostics.Debug.WriteLine($"QuickOpenFile NavigationBar parse: {ex}");
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_parseCts == cts)
                {
                    _parseCts = null;
                }
            }
        }

        private void UpdateContext()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_symbols == null)
            {
                return; // 첫 파싱이 끝나면 다시 호출된다.
            }

            CodeSymbol symbol = CodeSymbol.FindInnermost(_symbols, CaretLine);
            _contextText.Text = symbol?.ContextText ?? "(global scope)";
            _contextText.Opacity = symbol == null ? 0.6 : 1.0;
            _contextField.ToolTip = _contextText.Text + "\n클릭하면 파일의 함수 목록 (Alt+M)";
        }

        /// <summary>Visual Assist "List Methods in File" (Alt+M).</summary>
        public void ShowMethodList()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 사용자가 직접 연 목록은 항상 최신 내용으로 보여 준다. (파싱은 큰 파일도 수 ms)
            ITextSnapshot snapshot = _view.TextSnapshot;
            if (_symbols == null || _parsedVersion != snapshot.Version.VersionNumber)
            {
                _parseCts?.Cancel();
                _symbols = ScopeParser.Parse(snapshot.GetText(), _language);
                _parsedVersion = snapshot.Version.VersionNumber;
            }

            if (_symbols.Count == 0)
            {
                SetStatusBarText("List Methods in File: 이 파일에서 함수를 찾지 못했습니다.");
                return;
            }

            if (_popup != null)
            {
                _popup.IsOpen = false;
            }

            _popup = new MethodListPopup(_symbols, CodeSymbol.FindInnermost(_symbols, CaretLine), NavigateTo);
            if (_root.Visibility == Visibility.Visible)
            {
                _popup.PlacementTarget = _contextField;
                _popup.Placement = PlacementMode.Bottom;
            }
            else
            {
                _popup.PlacementTarget = _view.VisualElement;
                _popup.Placement = PlacementMode.Relative;
                _popup.HorizontalOffset = 20;
                _popup.VerticalOffset = 4;
            }

            _popup.IsOpen = true;
        }

        private void NavigateTo(CodeSymbol symbol)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ITextSnapshot snapshot = _view.TextSnapshot;
            int lineNumber = Math.Max(0, Math.Min(snapshot.LineCount - 1, symbol.StartLine - 1));
            ITextSnapshotLine line = snapshot.GetLineFromLineNumber(lineNumber);

            // 줄의 첫 번째 공백이 아닌 문자로 이동
            int position = line.Start.Position;
            while (position < line.End.Position && char.IsWhiteSpace(snapshot[position]))
            {
                position++;
            }

            var point = new SnapshotPoint(snapshot, position);
            _view.Caret.MoveTo(point);
            _view.Selection.Clear();
            _view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0), EnsureSpanVisibleOptions.AlwaysCenter);
            _view.VisualElement.Focus();
        }

        #endregion

        #region Definition 칸 (Quick Info)

        private async Task UpdateDefinitionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _definitionCts?.Cancel();
            var cts = new CancellationTokenSource();
            _definitionCts = cts;

            try
            {
                SnapshotPoint caret = _view.Caret.Position.BufferPosition;
                string definition = await QuickInfoReader.GetDefinitionAsync(_quickInfoBroker, _view, caret, cts.Token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (cts.IsCancellationRequested || _disposed)
                {
                    return;
                }

                // 심볼이 없는 위치(공백 등)에서는 마지막 선언을 흐리게 유지한다 (깜빡임 방지).
                if (definition != null)
                {
                    _definitionText.Text = definition;
                    _definitionText.Opacity = 1.0;
                    _definitionText.ToolTip = definition;
                }
                else
                {
                    _definitionText.Opacity = 0.5;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
            {
                System.Diagnostics.Debug.WriteLine($"QuickOpenFile NavigationBar: {ex}");
            }
        }

        #endregion
    }
}
